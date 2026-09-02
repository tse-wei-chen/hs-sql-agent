using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlServerDeleteUsingCapabilityTests
{
    private const string PostgresSql =
        "DELETE FROM users USING profiles WHERE users.id = profiles.user_id";

    [Fact]
    public void Compile_PostgresDeleteUsing_ToSqlServer_RendersJoinedDelete()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            PostgresSql,
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("postgres-delete-using-sqlserver-v1"));

        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.Contains("DELETE FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profiles", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SqlServerDeleteUsing_RemainsInvalidSourceGrammar()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                PostgresSql,
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("DELETE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DeleteUsingWithTargetAlias_ToSqlServer_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users AS u USING profiles WHERE u.id = profiles.user_id",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("postgres-delete-using-alias-sqlserver-v1")));

        Assert.Contains("dml.target_alias", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DeleteUsingReturning_ToSqlServer_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            PostgresSql + " RETURNING users.id",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("postgres-delete-using-returning-sqlserver-v1")));

        Assert.Contains("RETURNING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqlServerDeleteUsing_IsDeclaredTranslated()
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer).Capabilities,
            item => item.Id == "dml.delete.using");

        Assert.NotEqual(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains("SQL Server", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PostgreSQL", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
