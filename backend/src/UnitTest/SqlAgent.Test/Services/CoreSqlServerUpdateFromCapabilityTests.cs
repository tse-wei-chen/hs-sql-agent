using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlServerUpdateFromCapabilityTests
{
    private const string Sql =
        "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id";

    [Fact]
    public void Parse_SqlServerUpdateFrom_PreservesStructuredFromSource()
    {
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.MsSqlServer);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);

        Assert.Single(update.From);
        Assert.Null(update.TargetAlias);
        Assert.NotNull(update.Where);
    }

    [Fact]
    public void Compile_SqlServerUpdateFrom_EmitsNativeClause()
    {
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.MsSqlServer);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-update-from-v1"));

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profiles", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Parse_UpdateFromFromUnsupportedSourceDialect_FailsClosed(
        SqlAgentToolType sourceProvider)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(Sql, sourceProvider));

        Assert.Contains("dml.update.from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceProvider.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerUpdateFromToPostgres_RemainsNativeOnly()
    {
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.MsSqlServer);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("sqlserver-update-from-cross-v1")));

        Assert.Contains("dml.update.from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresUpdateFromToSqlServer_RemainsNativeOnly()
    {
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("postgres-update-from-cross-v1")));

        Assert.Contains("dml.update.from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_UpdateFrom_DeclaresSqlServerNativeSupport()
    {
        var sqlServer = Capability(SqlAgentToolType.MsSqlServer);
        var postgres = Capability(SqlAgentToolType.Postgres);
        var mysql = Capability(SqlAgentToolType.MySQL);

        Assert.Equal(SqlCapabilityStatus.Supported, sqlServer.Status);
        Assert.NotEqual(SqlCapabilityStatus.Rejected, postgres.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, mysql.Status);
    }

    private static SqlCapability Capability(SqlAgentToolType provider) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "dml.update.from");
}
