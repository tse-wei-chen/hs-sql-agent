using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSchemaSnapshotTypeValidationTests
{
    public static IEnumerable<object[]> Providers()
        => Enum.GetValues<SqlAgentToolType>().Select(dialect => new object[] { dialect });

    private static SqlPlanValidationContext Context(SqlAgentToolType dialect, bool typed)
    {
        var upper = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string name) => upper ? name.ToUpperInvariant() : name;
        var columns = new[]
        {
            new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric),
            new SqlSchemaColumn(Name("name"), SqlSchemaValueKind.Text),
            new SqlSchemaColumn(Name("enabled"), SqlSchemaValueKind.Boolean)
        };
        var table = typed
            ? SqlSchemaTable.WithTypes(Name("users"), columns)
            : new SqlSchemaTable(Name("users"), columns.Select(x => x.Name).ToArray());
        return new SqlPlanValidationContext("typed-schema-v1").WithSchemaSnapshot(
            new SqlSchemaSnapshot(dialect, "catalog-v1", new[] { table }));
    }

    public static IEnumerable<object[]> Rejections()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            yield return new object[] { dialect, "SELECT id FROM users WHERE id = 'not-a-number'", false };
            yield return new object[] { dialect, "SELECT id FROM users WHERE id = name", false };
            yield return new object[] { dialect, "SELECT id FROM users WHERE name + 2 > 0", false };
            yield return new object[] { dialect, "UPDATE users SET name = 3 WHERE id = 7", true };
            yield return new object[] { dialect, "INSERT INTO users (name) VALUES (3)", true };
            yield return new object[] { dialect, "DELETE FROM users WHERE id = 'bad'", true };
        }
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void TypedSnapshot_RejectsProvenIncompatibleTypeFamilies(
        SqlAgentToolType dialect, string sql, bool dml)
    {
        var context = Context(dialect, typed: true);
        var error = Assert.Throws<SqlCompilationException>(() =>
        {
            if (dml)
                SqlCoreFacade.CompileDml(sql, dialect, dialect, context);
            else
                SqlCoreFacade.CompileQuery(
                    sql, dialect, dialect, context, new SqlExecutionPlanPolicy());
        });

        Assert.NotNull(error.Diagnostic);
        Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", error.Diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, error.Diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Semantic, error.Diagnostic.Category);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TypedSnapshot_PreservesValidNumericAndTextPredicatesAndAssignments(
        SqlAgentToolType dialect)
    {
        var context = Context(dialect, typed: true);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            "SELECT id, name FROM users WHERE id = 7 AND name = 'Alice'",
            dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql);

        Assert.NotEmpty(SqlCoreFacade.CompileDml(
            "UPDATE users SET name = 'Alice' WHERE id = 7",
            dialect, dialect, context).Sql);

        Assert.NotEmpty(SqlCoreFacade.CompileDml(
            "INSERT INTO users (id, name) VALUES (7, 'Alice')",
            dialect, dialect, context).Sql);

        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users WHERE id = 7", dialect);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            parsed, dialect, context, new SqlExecutionPlanPolicy()).Sql);
    }

    [Fact]
    public void NameOnlySchema_StillAcceptsPreviouslyUnprovenTypeCoercions()
    {
        var sql = "SELECT id FROM users WHERE id = 'bad'";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var command = SqlCoreFacade.CompileQuery(
            parsed, SqlAgentToolType.Postgres,
            Context(SqlAgentToolType.Postgres, typed: false),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void SchemaBoundDmlFingerprint_IsRecomputableAtPreviewAndCommit(
        SqlAgentToolType dialect)
    {
        var context = Context(dialect, typed: true);
        var command = SqlCoreFacade.CompileDml(
            "DELETE FROM users WHERE id = 7", dialect, dialect, context);

        // Preview/commit intentionally compute the fingerprint from the approved command.
        // The schema proof must not disappear at that boundary.
        Assert.Equal(
            command.PlanFingerprint,
            HsSqlAgent.SqlCore.Core.Execution.DmlFingerprintService.ComputePlanFingerprint(
                command, context.PolicyVersion));
        Assert.NotNull(command.CompileEvidence);
        Assert.Contains(command.CompileEvidence!.Assurances,
            assurance => assurance.Kind == "sqlcore.schema_snapshot");
    }

    [Fact]
    public void TypeProofChangesAlterPlanAndEvidenceIdentity()
    {
        var typed = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users", SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, Context(SqlAgentToolType.Postgres, typed: true),
            new SqlExecutionPlanPolicy());
        var untyped = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users", SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, Context(SqlAgentToolType.Postgres, typed: false),
            new SqlExecutionPlanPolicy());
        Assert.NotEqual(typed.PlanFingerprint, untyped.PlanFingerprint);
        Assert.NotNull(typed.CompileEvidence);
        Assert.NotNull(untyped.CompileEvidence);
        Assert.NotEqual(typed.CompileEvidence!.EvidenceFingerprint,
            untyped.CompileEvidence!.EvidenceFingerprint);
    }

    [Fact]
    public void TypedColumnInputs_AreDefensivelyCopiedAndValidated()
    {
        var input = new List<SqlSchemaColumn>
        {
            new("id", SqlSchemaValueKind.Numeric)
        };
        var table = SqlSchemaTable.WithTypes("users", input);
        input[0] = new SqlSchemaColumn("other", SqlSchemaValueKind.Text);
        Assert.Equal("id", table.TypedColumns[0].Name);
        Assert.Equal(SqlSchemaValueKind.Numeric, table.TypedColumns[0].Kind);
        Assert.Throws<ArgumentException>(() =>
            new SqlSchemaColumn("bad", (SqlSchemaValueKind)999));
    }
}
