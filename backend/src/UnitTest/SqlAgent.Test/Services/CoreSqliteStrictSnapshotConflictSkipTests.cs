using HsSqlAgent.SqlCore.Core.Pipeline;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqliteStrictSnapshotConflictSkipTests
{
    private static readonly SqlProviderCapabilityProfile Profile =
        new(SqlAgentToolType.Sqlite, new Version(3, 46));

    private static SqlPlanValidationContext Context(SqlAgentToolType dialect) =>
        new SqlPlanValidationContext("m10-native-schema-skip-v1").WithSchemaSnapshot(
            new SqlSchemaSnapshot(dialect, "physical-catalog-fixture-v1", [
                new SqlSchemaTable("users", ["id", "name"])
            ]));

    [Fact]
    public void SqliteStrictSnapshot_SingleRowExplicitDoNothing_ProvesPhysicalColumnShapeOnly()
    {
        const string sql =
            "INSERT INTO users (id, name) VALUES (7, 'new-name') ON CONFLICT (id) DO NOTHING";
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite, Profile);
        var command = CoreDmlCompiler.CreateDefault().Compile(parsed,
            SqlAgentToolType.Sqlite, Context(SqlAgentToolType.Sqlite), targetProfile: Profile);
        Assert.Contains("ON CONFLICT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SqlStatementKind.Insert, command.Kind);
    }

    [Theory]
    [InlineData("INSERT INTO users (id, name) VALUES (7,'n') ON CONFLICT (id) DO UPDATE SET name=excluded.name")]
    [InlineData("INSERT INTO users (id, name) VALUES (7,'n'),(8,'y') ON CONFLICT (id) DO NOTHING")]
    [InlineData("INSERT INTO users (id, name) VALUES (7,'n') ON CONFLICT DO NOTHING")]
    [InlineData("INSERT INTO users (id, name) VALUES (7,'n') ON CONFLICT (id) DO NOTHING RETURNING id")]
    public void SqliteStrictSnapshot_UnprovedConflictShape_StillFailsClosed(string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite, Profile);
        var failure = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(parsed, SqlAgentToolType.Sqlite,
                Context(SqlAgentToolType.Sqlite), targetProfile: Profile));
        Assert.Contains("Strict snapshot", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgresStrictSnapshot_CannotBorrowSQLiteStructuralException()
    {
        const string sql =
            "INSERT INTO users (id, name) VALUES (7,'n') ON CONFLICT (id) DO NOTHING";
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        var failure = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(parsed, SqlAgentToolType.Postgres,
                Context(SqlAgentToolType.Postgres)));
        Assert.Contains("Strict snapshot", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqliteStrictSnapshot_UnknownConflictKey_RejectsBeforeRendering()
    {
        const string sql =
            "INSERT INTO users (id, unknown) VALUES (7,'n') ON CONFLICT (unknown) DO NOTHING";
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite, Profile);
        Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(parsed, SqlAgentToolType.Sqlite,
                Context(SqlAgentToolType.Sqlite), targetProfile: Profile));
    }
}
