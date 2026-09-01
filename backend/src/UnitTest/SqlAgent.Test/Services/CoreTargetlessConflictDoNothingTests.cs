using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreTargetlessConflictDoNothingTests
{
    private const string PostgresSql =
        "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT DO NOTHING";

    [Fact]
    public void Parse_PostgresTargetlessDoNothing_ModelsEmptyConflictTarget()
    {
        var parsed = CoreSqlTextParser.ParseDml(PostgresSql, SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var conflict = Assert.IsType<InsertConflictClause>(insert.Conflict);

        Assert.Equal(InsertConflictActionKind.DoNothing, conflict.Action);
        Assert.Empty(conflict.TargetColumns);
        Assert.Empty(conflict.Assignments);
    }

    [Fact]
    public void Compile_PostgresTargetlessDoNothing_EmitsNativeClauseWithoutInventedTarget()
    {
        var parsed = CoreSqlTextParser.ParseDml(PostgresSql, SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("targetless-conflict-pg-v1"));

        Assert.Contains("ON CONFLICT DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT (", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresInsertSelectTargetlessDoNothing_PreservesNativeSemantics()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) " +
            "SELECT id, name FROM staged_users ON CONFLICT DO NOTHING",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("targetless-conflict-pg-select-v1"));

        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON CONFLICT DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_TargetlessDoUpdate_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
                "ON CONFLICT DO UPDATE SET name = excluded.name",
                SqlAgentToolType.Postgres));

        Assert.Contains("DO UPDATE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit conflict target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresTargetlessDoNothingToSqlite_FailsAsNativeOnly()
    {
        var parsed = CoreSqlTextParser.ParseDml(PostgresSql, SqlAgentToolType.Postgres);
        var sqlite = Profile(SqlAgentToolType.Sqlite, 3, 24);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("targetless-conflict-cross-v1"),
                targetProfile: sqlite));

        Assert.Contains("dml.conflict_do_nothing_any", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Postgres", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sqlite", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqliteTargetlessDoNothing_RequiresSourceAndTargetVersion324()
    {
        const string sql =
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT DO NOTHING";

        var sourceError = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite));
        Assert.Contains("3.24", sourceError.Message, StringComparison.OrdinalIgnoreCase);

        var profile = Profile(SqlAgentToolType.Sqlite, 3, 24);
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite, profile);

        var targetError = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("targetless-conflict-sqlite-v1")));
        Assert.Contains("3.24", targetError.Message, StringComparison.OrdinalIgnoreCase);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("targetless-conflict-sqlite-v1"),
            targetProfile: profile);

        Assert.Contains("ON CONFLICT DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT (", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ExplicitTargetDoNothing_RemainsSupported()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("targeted-conflict-regression-v1"));

        Assert.Contains("ON CONFLICT (", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO NOTHING", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_TargetlessDoNothing_TracksNativeProviderCapability()
    {
        Assert.Equal(
            SqlCapabilityStatus.Translated,
            Capability(SqlAgentToolType.Postgres, null).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(SqlAgentToolType.Sqlite, null).Status);
        Assert.Equal(
            SqlCapabilityStatus.Translated,
            Capability(
                SqlAgentToolType.Sqlite,
                Profile(SqlAgentToolType.Sqlite, 3, 24)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(SqlAgentToolType.MySQL, null).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(SqlAgentToolType.Firebird, null).Status);
    }

    private static SqlCapability Capability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            item => item.Id == "dml.conflict_do_nothing_any");

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor) =>
        new(provider, ServerVersion: new Version(major, minor));
}
