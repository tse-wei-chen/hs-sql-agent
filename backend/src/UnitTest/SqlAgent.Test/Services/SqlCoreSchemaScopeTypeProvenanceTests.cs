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
/// A known-name / unknown-type inner field must shadow outer typed fields.
/// Only a successfully resolved, type-known source may provide family evidence.
/// Name ambiguity must never be interpreted as a successfully typed lookup.
/// </summary>
public sealed class SqlCoreSchemaScopeTypeProvenanceTests
{
    public static IEnumerable<object[]> Providers() =>
        Enum.GetValues<SqlAgentToolType>().Select(x => new object[] { x });

    private static SqlPlanValidationContext Snapshot(SqlAgentToolType dialect)
    {
        var uppercase = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string name) => uppercase ? name.ToUpperInvariant() : name;

        var known = SqlSchemaTable.WithTypes(Name("users"), new[]
        {
            new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric),
            new SqlSchemaColumn(Name("description"), SqlSchemaValueKind.Text)
        });
        // A name-only Snapshot proves this table and these column names,
        // but deliberately provides NO primitive or family type evidence.
        var unknown = new SqlSchemaTable(Name("shadow"), new[]
        {
            Name("id"), Name("payload")
        });
        var missingId = new SqlSchemaTable(Name("shadow_without_id"), new[]
        {
            Name("payload")
        });
        return new SqlPlanValidationContext("scope-provenance-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(dialect, "scope-provenance-v1",
                new[] { known, unknown, missingId }));
    }

    private static void Compiles(SqlAgentToolType dialect, string sql)
    {
        var result = SqlCoreFacade.CompileQuery(sql, dialect, dialect,
            Snapshot(dialect), new SqlExecutionPlanPolicy());
        Assert.NotEmpty(result.Sql);
    }

    private static void RejectsProvenMismatch(SqlAgentToolType dialect, string sql)
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(sql, dialect, dialect,
                Snapshot(dialect), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", ex.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, ex.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Semantic, ex.Diagnostic?.Category);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void NameOnlyColumnDoesNotBecomeProvenNumericAcrossSixProviders(SqlAgentToolType dialect)
    {
        Compiles(dialect, "SELECT s.id FROM shadow s WHERE s.id = 'text'");
        Compiles(dialect, "SELECT s.id FROM shadow s WHERE s.id + 1 = 2");
        RejectsProvenMismatch(dialect, "SELECT u.id FROM users u WHERE u.id = 'text'");
        RejectsProvenMismatch(dialect, "SELECT u.id FROM users u WHERE u.description + 1 = 2");
    }

    [Theory]
    [InlineData("SELECT u.id FROM users u WHERE EXISTS (SELECT 1 FROM shadow s WHERE s.id = 'text')")]
    [InlineData("SELECT u.id FROM users u WHERE EXISTS (SELECT 1 FROM shadow s WHERE id = 'text')")]
    [InlineData("SELECT u.id FROM users u WHERE EXISTS (SELECT 1 FROM shadow s WHERE s.id + 1 = 2)")]
    public void InnerUnknownNameTypeShadowsOuterKnownType(string sql)
    {
        Compiles(SqlAgentToolType.Postgres, sql);
    }

    [Theory]
    [InlineData("SELECT u.id FROM users u WHERE EXISTS (SELECT 1 FROM shadow s WHERE u.id = 'text')")]
    [InlineData("SELECT u.id FROM users u WHERE EXISTS (SELECT 1 FROM shadow_without_id s WHERE id = 'text')")]
    [InlineData("SELECT u.id FROM users u WHERE EXISTS (SELECT 1 WHERE u.id = 'text')")]
    public void RealOuterColumnProofRemainsVisibleAcrossCorrelatedScope(string sql)
    {
        RejectsProvenMismatch(SqlAgentToolType.Postgres, sql);
    }

    [Theory]
    [InlineData("WITH c AS (SELECT id FROM shadow) SELECT id FROM c WHERE id = 'text'")]
    [InlineData("SELECT d.id FROM (SELECT id FROM shadow) d WHERE d.id = 'text'")]
    [InlineData("SELECT d.id FROM (SELECT id FROM shadow) d WHERE d.id + 1 = 2")]
    public void UnknownTypePropagatesWithoutUpgradingThroughCteOrDerivedProjection(string sql)
    {
        Compiles(SqlAgentToolType.Postgres, sql);
    }

    [Theory]
    [InlineData("WITH c AS (SELECT id FROM users) SELECT id FROM c WHERE id = 'text'")]
    [InlineData("SELECT d.id FROM (SELECT id FROM users) d WHERE d.id = 'text'")]
    [InlineData("SELECT d.id FROM (SELECT description FROM users) d WHERE d.description + 1 = 2")]
    public void ProvenTypePropagatesAndRejectsWrongFamiliesThroughCteOrDerivedProjection(string sql)
    {
        RejectsProvenMismatch(SqlAgentToolType.Postgres, sql);
    }

    [Fact]
    public void AmbiguousJoinedNamesNeverAcquireOneSideTypeEvidence()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM users u JOIN shadow s ON TRUE WHERE id = 'text'",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Snapshot(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()));
        Assert.NotNull(ex.Diagnostic);
        Assert.NotEqual("SQL_SCHEMA_TYPE_MISMATCH", ex.Diagnostic!.Code);
    }

    [Fact]
    public void UnknownTypeStillRequiresANameBindingProof()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT unknown_field FROM shadow s",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                Snapshot(SqlAgentToolType.Postgres), new SqlExecutionPlanPolicy()));
        Assert.NotNull(ex.Diagnostic);
        Assert.NotEqual("SQL_SCHEMA_TYPE_MISMATCH", ex.Diagnostic!.Code);
    }
}
