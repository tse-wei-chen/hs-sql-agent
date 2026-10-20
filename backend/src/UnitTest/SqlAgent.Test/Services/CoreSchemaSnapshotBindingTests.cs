using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSchemaSnapshotBindingTests
{
    private static readonly SqlAgentToolType[] Providers = Enum.GetValues<SqlAgentToolType>();

    private static SqlPlanValidationContext Context(SqlAgentToolType dialect)
    {
        var upper = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        var snapshot = new SqlSchemaSnapshot(
            dialect,
            "schema-v1",
            new[]
            {
                new SqlSchemaTable(upper ? "USERS" : "users",
                    upper ? new[] { "ID", "NAME", "ACTIVE" } : new[] { "id", "name", "active" }),
                new SqlSchemaTable(upper ? "ORDERS" : "orders",
                    upper ? new[] { "ID", "USER_ID" } : new[] { "id", "user_id" })
            });
        return new SqlPlanValidationContext("schema-policy-v1").WithSchemaSnapshot(snapshot);
    }

    public static IEnumerable<object[]> SixDialects()
        => Providers.Select(provider => new object[] { provider });

    public static IEnumerable<object[]> RejectedReferences()
    {
        foreach (var dialect in Providers)
        {
            yield return new object[]
            {
                dialect, "SELECT missing FROM users", "SQL_SCHEMA_COLUMN_NOT_FOUND"
            };
            yield return new object[]
            {
                dialect, "SELECT id FROM unavailable", "SQL_SCHEMA_TABLE_NOT_FOUND"
            };
            yield return new object[]
            {
                dialect, "SELECT id FROM users u JOIN orders o ON u.id = o.user_id",
                "SQL_SCHEMA_COLUMN_AMBIGUOUS"
            };
            yield return new object[]
            {
                dialect, "SELECT u.missing FROM users u", "SQL_SCHEMA_COLUMN_NOT_FOUND"
            };
        }
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void AllDialects_ValidateExistingQualifiedAndUnqualifiedColumns(SqlAgentToolType dialect)
    {
        var context = Context(dialect);
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT u.id, u.name FROM users u WHERE u.active = true",
            dialect, dialect, context, new SqlExecutionPlanPolicy(10));
        Assert.NotEmpty(compiled.Sql);
        Assert.NotEmpty(compiled.Parameters);

        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT u.id FROM users u WHERE u.id = 7", dialect);
        var fromParsed = CoreSqlFacadeCompileParsed(parsed, dialect, context);
        Assert.NotEmpty(fromParsed.Sql);
    }

    private static CompiledSqlCommand CoreSqlFacadeCompileParsed(
        ParsedStatement parsed, SqlAgentToolType dialect, SqlPlanValidationContext context) =>
        SqlCoreFacade.CompileQuery(parsed, dialect, context, new SqlExecutionPlanPolicy(10));

    [Theory]
    [MemberData(nameof(RejectedReferences))]
    public void AllDialects_RejectUnprovenSchemaReferencesWithTypedBindingDiagnostics(
        SqlAgentToolType dialect, string sql, string expectedCode)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, Context(dialect), new SqlExecutionPlanPolicy(10)));

        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Binding, diagnostic.Category);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void AllDialects_DmlTargetColumnsAndPredicatesRequireSnapshotProof(
        SqlAgentToolType dialect)
    {
        Assert.NotEmpty(SqlCoreFacade.CompileDml(
            "UPDATE users SET name = 'Updated' WHERE id = 1",
            dialect, dialect, Context(dialect)).Sql);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(
                "DELETE FROM users WHERE missing = 1",
                dialect, dialect, Context(dialect)));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", ex.Diagnostic?.Code);

        var insert = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(
                "INSERT INTO users (missing) VALUES (3)",
                dialect, dialect, Context(dialect)));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", insert.Diagnostic?.Code);
    }

    [Fact]
    public void Postgres_CteAndCorrelatedQueries_UseActualScopedColumns()
    {
        var context = Context(SqlAgentToolType.Postgres);
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            "WITH x AS (SELECT id, name FROM users) SELECT x.name FROM x",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy()).Sql);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT u.id FROM users u WHERE EXISTS " +
                "(SELECT 1 FROM orders o WHERE o.user_id = u.id AND o.missing = 1)",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_COLUMN_NOT_FOUND", ex.Diagnostic?.Code);
    }

    [Fact]
    public void Postgres_SnapshotValidation_RefusesRecursiveCteAndUnprovenProjection()
    {
        var context = Context(SqlAgentToolType.Postgres);
        var recursive = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM x WHERE n < 3) " +
                "SELECT n FROM x",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_RECURSIVE_CTE_UNPROVABLE", recursive.Diagnostic?.Code);

        var computed = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT x.anonymous FROM (SELECT id + 1 FROM users) x",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                context, new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_SHAPE_UNPROVABLE", computed.Diagnostic?.Code);
    }

    [Fact]
    public void MissingSnapshot_RetainsExistingCompilerCompatibility()
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT unknown_field FROM users",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("legacy-scope-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void SnapshotCopiesInputColumnsAndRejectsWrongDialect()
    {
        var cols = new List<string> { "id" };
        var table = new SqlSchemaTable("users", cols);
        cols[0] = "changed";
        Assert.Equal("id", table.Columns[0]);
        var schema = new SqlSchemaSnapshot(SqlAgentToolType.Postgres, "stable-v1", new[] { table });
        Assert.Equal("stable-v1", schema.Version);

        Assert.Throws<ArgumentException>(() => SqlCoreFacade.CompileQuery(
            "SELECT id FROM users",
            SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mismatched").WithSchemaSnapshot(schema),
            new SqlExecutionPlanPolicy()));
    }
}
