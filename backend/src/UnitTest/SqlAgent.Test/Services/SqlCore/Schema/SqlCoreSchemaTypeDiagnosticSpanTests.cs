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
/// Type-family incompatibilities remain a proven-only, fail-closed semantic
/// rejection. A diagnostic must identify the actual expression in the SQL,
/// not always the entire INSERT/UPDATE statement.
/// </summary>
public sealed class SqlCoreSchemaTypeDiagnosticSpanTests
{
    public static IEnumerable<object[]> Providers() =>
        Enum.GetValues<SqlAgentToolType>().Select(p => new object[] { p });

    private static SqlPlanValidationContext Typed(SqlAgentToolType provider)
    {
        var upper = provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string value) => upper ? value.ToUpperInvariant() : value;
        return new SqlPlanValidationContext("diagnostic-origin-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(provider, "diagnostic-origin-v1",
                new[] { SqlSchemaTable.WithTypes(Name("items"), new[]
                {
                    new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric),
                    new SqlSchemaColumn(Name("description"), SqlSchemaValueKind.Text),
                    new SqlSchemaColumn(Name("active"), SqlSchemaValueKind.Boolean)
                }) }));
    }

    private static SqlCompilationException Reject(SqlAgentToolType dialect, string sql, bool dml)
    {
        return Assert.Throws<SqlCompilationException>(() =>
        {
            if (dml)
                SqlCoreFacade.CompileDml(sql, dialect, dialect, Typed(dialect));
            else
                SqlCoreFacade.CompileQuery(sql, dialect, dialect, Typed(dialect),
                    new SqlExecutionPlanPolicy());
        });
    }

    private static void AssertPrecise(SqlCompilationException ex, string sql, string expression)
    {
        Assert.NotNull(ex.Diagnostic);
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", ex.Diagnostic!.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, ex.Diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Semantic, ex.Diagnostic.Category);
        var span = Assert.IsType<SqlDiagnosticSpan>(ex.Diagnostic.Span);
        Assert.True(span.Length > 0, "Type diagnostic must have a positive SourceSpan.");
        Assert.InRange(span.Start, 0, sql.Length - 1);
        Assert.True(span.End <= sql.Length);
        Assert.Contains(expression, sql.Substring(span.Start, span.Length), StringComparison.OrdinalIgnoreCase);
        Assert.True(span.Length < sql.Length, "Diagnostic must not highlight the full statement.");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void DmlAssignmentHighlightsFailingExpressionInAllSixDialects(SqlAgentToolType provider)
    {
        const string update = "UPDATE items SET description = 42 WHERE id = 7";
        AssertPrecise(Reject(provider, update, true), update, "42");

        const string insert = "INSERT INTO items (description) VALUES (42)";
        AssertPrecise(Reject(provider, insert, true), insert, "42");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void QueryTypeMismatchKeepsExpressionEvidenceAndSourceLocation(SqlAgentToolType provider)
    {
        const string comparison = "SELECT id FROM items WHERE id = 'not-a-number'";
        AssertPrecise(Reject(provider, comparison, false), comparison, "id = 'not-a-number'");

        const string arithmetic = "SELECT id FROM items WHERE description + 2 > 0";
        AssertPrecise(Reject(provider, arithmetic, false), arithmetic, "description + 2");
    }

    [Theory]
    [InlineData("SELECT u.id FROM items u WHERE EXISTS (SELECT 1 WHERE u.id = 'no')", "u.id = 'no'")]
    [InlineData("WITH x AS (SELECT id FROM items) SELECT id FROM x WHERE id = 'no'", "id = 'no'")]
    [InlineData("SELECT d.id FROM (SELECT id FROM items) d WHERE d.id = 'no'", "d.id = 'no'")]
    public void NestedScopesRetainAbsoluteOrigin(string sql, string expression)
    {
        AssertPrecise(Reject(SqlAgentToolType.Postgres, sql, false), sql, expression);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void UntypedMetadataCannotFabricateFamilyMismatch(SqlAgentToolType provider)
    {
        var name = provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird ? "ITEMS" : "items";
        var unknown = new SqlPlanValidationContext("diagnostic-untyped-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(provider, "diagnostic-untyped-v1",
                new[] { new SqlSchemaTable(name, new[]
                {
                    provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird ? "DESCRIPTION" : "description"
                }) }));
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT description FROM items WHERE description + 2 > 0",
            provider, provider, unknown, new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
    }
}
