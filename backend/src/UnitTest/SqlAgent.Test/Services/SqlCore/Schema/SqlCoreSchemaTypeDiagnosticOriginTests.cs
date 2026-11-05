using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Static family evidence is attributed only when a mismatch is proven.
/// Unknown type metadata does not authorize a mismatch diagnostic.
/// </summary>
public sealed class SqlCoreSchemaTypeDiagnosticOriginTests
{
    public static IEnumerable<object[]> Providers() =>
        Enum.GetValues<SqlAgentToolType>().Select(d => new object[] { d });

    private static SqlPlanValidationContext Typed(SqlAgentToolType provider)
    {
        var upper = provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string name) => upper ? name.ToUpperInvariant() : name;
        return new SqlPlanValidationContext("diagnostic-evidence-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(provider, "diagnostic-evidence-v1",
                new[]
                {
                    SqlSchemaTable.WithTypes(Name("items"), new[]
                    {
                        new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn(Name("description"), SqlSchemaValueKind.Text),
                        new SqlSchemaColumn(Name("active"), SqlSchemaValueKind.Boolean)
                    })
                }));
    }

    private static SqlCompilationException Failure(
        SqlAgentToolType provider, string sql, bool dml = false) =>
        Assert.Throws<SqlCompilationException>(() =>
        {
            if (dml) SqlCoreFacade.CompileDml(sql, provider, provider, Typed(provider));
            else SqlCoreFacade.CompileQuery(sql, provider, provider,
                Typed(provider), new SqlExecutionPlanPolicy());
        });

    private static void Check(SqlCompilationException exception, string column)
    {
        var diagnostic = Assert.IsType<SqlDiagnostic>(exception.Diagnostic);
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", diagnostic.Code);
        Assert.Equal(SqlDiagnosticCategory.Semantic, diagnostic.Category);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, diagnostic.Stage);
        Assert.Contains(column, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scope depth", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proven", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void SixDialectsReportBothSidesOfProvenComparison(SqlAgentToolType provider)
    {
        var error = Failure(provider, "SELECT id FROM items WHERE id = 'bad'");
        Check(error, "id");
        Assert.Contains("literal", error.Diagnostic!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Numeric", error.Diagnostic.Message);
        Assert.Contains("Text", error.Diagnostic.Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void SixDialectsReportArithmeticColumnProvenance(SqlAgentToolType provider)
    {
        Check(Failure(provider, "SELECT id FROM items WHERE description + 2 > 0"),
            "description");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void SixDialectsReportUpdateAndInsertTargetEvidence(SqlAgentToolType provider)
    {
        var update = Failure(provider,
            "UPDATE items SET description = 42 WHERE id = 7", true);
        Assert.Contains("description", update.Diagnostic!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assignment target", update.Diagnostic.Message);
        Assert.Contains("literal", update.Diagnostic.Message);

        var insert = Failure(provider,
            "INSERT INTO items (description) VALUES (42)", true);
        Assert.Contains("description", insert.Diagnostic!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assignment target", insert.Diagnostic.Message);
        Assert.Contains("literal", insert.Diagnostic.Message);
    }

    [Theory]
    [InlineData("SELECT u.id FROM items u WHERE EXISTS (SELECT 1 WHERE u.id = 'bad')", "u.id", 1)]
    [InlineData("WITH x AS (SELECT id FROM items) SELECT id FROM x WHERE id = 'bad'", "id", 0)]
    [InlineData("SELECT d.id FROM (SELECT id FROM items) d WHERE d.id = 'bad'", "d.id", 0)]
    public void NestedScopesRetainKnownFamilyOrigin(string sql, string name, int depth)
    {
        var error = Failure(SqlAgentToolType.Postgres, sql);
        Check(error, name);
        Assert.Contains("scope depth " + depth, error.Diagnostic!.Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void UnknownFamilyDoesNotProduceSpeculativeErrorOrProvenance(SqlAgentToolType provider)
    {
        var upper = provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string s) => upper ? s.ToUpperInvariant() : s;
        var untyped = new SqlPlanValidationContext("evidence-untyped")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(provider, "evidence-untyped",
                new[]
                {
                    new SqlSchemaTable(Name("items"), new[]
                    {
                        Name("id"), Name("description")
                    })
                }));
        var result = SqlCoreFacade.CompileQuery(
            "SELECT id FROM items WHERE description + 3 > 1",
            provider, provider, untyped, new SqlExecutionPlanPolicy());
        Assert.NotEmpty(result.Sql);
    }

    [Fact]
    public void AmbiguousNameIsRejectedByBindingNotTypeGuesswork()
    {
        var ctx = Typed(SqlAgentToolType.Postgres);
        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM items a JOIN items b ON TRUE WHERE id = 'bad'",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                ctx, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_AMBIGUOUS", ex.Diagnostic?.Code);
    }
}
