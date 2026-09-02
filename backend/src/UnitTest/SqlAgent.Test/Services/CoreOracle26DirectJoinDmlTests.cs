using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreOracle26DirectJoinDmlTests
{
    private const string UpdateSql =
        "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id";
    private const string DeleteSql =
        "DELETE FROM users FROM profiles WHERE users.id = profiles.user_id";

    private static SqlProviderCapabilityProfile Oracle26() =>
        new(SqlAgentToolType.Oracle, ServerVersion: new Version(26, 0));

    [Fact]
    public void Parse_OracleDirectJoin_RequiresExplicitVersion26()
    {
        var updateError = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(UpdateSql, SqlAgentToolType.Oracle));
        Assert.Contains("26.0", updateError.Message, StringComparison.OrdinalIgnoreCase);

        var deleteError = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(DeleteSql, SqlAgentToolType.Oracle));
        Assert.Contains("26.0", deleteError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Oracle26UpdateFrom_RequiresTargetVersion26()
    {
        var parsed = CoreSqlTextParser.ParseDml(UpdateSql, SqlAgentToolType.Oracle, Oracle26());

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("oracle26-update-from-target-v1")));

        Assert.Contains("26.0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Oracle26UpdateFrom_RendersNativeAndStaysNativeOnly()
    {
        var profile = Oracle26();
        var parsed = CoreSqlTextParser.ParseDml(UpdateSql, SqlAgentToolType.Oracle, profile);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("oracle26-update-from-v1"),
            targetProfile: profile);

        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profiles", command.Sql, StringComparison.OrdinalIgnoreCase);

        var cross = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("oracle26-update-from-cross-v1")));

        Assert.Contains("dml.update.from", cross.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-only", cross.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Oracle26DeleteFrom_RendersNative()
    {
        var profile = Oracle26();
        var parsed = CoreSqlTextParser.ParseDml(DeleteSql, SqlAgentToolType.Oracle, profile);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("oracle26-delete-from-v1"),
            targetProfile: profile);

        Assert.Contains("DELETE FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profiles", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JoinedDelete_CrossLowersBetweenPostgresAndOracle26()
    {
        var postgres = CoreSqlTextParser.ParseDml(
            "DELETE FROM users USING profiles WHERE users.id = profiles.user_id",
            SqlAgentToolType.Postgres);
        var toOracle = CoreDmlCompiler.CreateDefault().Compile(
            postgres,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("postgres-delete-oracle26-v1"),
            targetProfile: Oracle26());
        Assert.Contains(" FROM ", toOracle.Sql, StringComparison.OrdinalIgnoreCase);

        var oracle = CoreSqlTextParser.ParseDml(DeleteSql, SqlAgentToolType.Oracle, Oracle26());
        var toPostgres = CoreDmlCompiler.CreateDefault().Compile(
            oracle,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("oracle26-delete-postgres-v1"));
        Assert.Contains("USING", toPostgres.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_Oracle26DeclaresDirectJoinDml()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Oracle, Oracle26());
        var update = Assert.Single(matrix.Capabilities, item => item.Id == "dml.update.from");
        var delete = Assert.Single(matrix.Capabilities, item => item.Id == "dml.delete.using");

        Assert.NotEqual(SqlCapabilityStatus.Rejected, update.Status);
        Assert.NotEqual(SqlCapabilityStatus.Rejected, delete.Status);
        Assert.Contains("26", update.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("26", delete.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
