using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlServerOutputCapabilityTests
{
    [Theory]
    [InlineData(DmlOperation.Insert, "INSERT INTO users (id, name) OUTPUT INSERTED.id VALUES (1, 'Alice')", "OUTPUT INSERTED.")]
    [InlineData(DmlOperation.Update, "UPDATE users SET name = 'Alice' OUTPUT INSERTED.id WHERE id = 1", "OUTPUT INSERTED.")]
    [InlineData(DmlOperation.Delete, "DELETE FROM users OUTPUT DELETED.id WHERE id = 1", "OUTPUT DELETED.")]
    public void Compile_Output_WithExactNoTriggerAssurance_RendersNativeRows(
        DmlOperation operation,
        string sql,
        string outputFragment)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            Context("users", operation));

        Assert.True(command.ReturnsRows);
        Assert.Contains(outputFragment, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" RETURNING ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Output_WithoutNoTriggerAssurance_FailsTargetCapability()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET name = 'Alice' OUTPUT INSERTED.id WHERE id = 1",
            SqlAgentToolType.MsSqlServer);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("sqlserver-output-missing-assurance-v1")));

        Assert.Contains("OUTPUT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enabled trigger", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assurance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("audit_users", DmlOperation.Update)]
    [InlineData("users", DmlOperation.Delete)]
    public void Compile_Output_WithMismatchedAssurance_FailsClosed(
        string assuredTable,
        DmlOperation assuredOperation)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET name = 'Alice' OUTPUT INSERTED.id WHERE id = 1",
            SqlAgentToolType.MsSqlServer);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                Context(assuredTable, assuredOperation)));

        Assert.Contains("dml.returning_output", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assurance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO users (id) OUTPUT DELETED.id VALUES (1)", "INSERTED")]
    [InlineData("UPDATE users SET id = 2 OUTPUT DELETED.id WHERE id = 1", "INSERTED")]
    [InlineData("DELETE FROM users OUTPUT INSERTED.id WHERE id = 1", "DELETED")]
    public void Parse_Output_WithOppositeRowImage_FailsClosed(
        string sql,
        string expectedImage)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer));

        Assert.Contains("OUTPUT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedImage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SqlServerReturningSpelling_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "DELETE FROM users WHERE id = 1 RETURNING id",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("OUTPUT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RETURNING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_OutputRichExpression_RemainsFailClosed()
    {
        Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "UPDATE users SET id = 2 OUTPUT INSERTED.id + 1 WHERE id = 1",
                SqlAgentToolType.MsSqlServer));
    }

    [Fact]
    public void Compile_OutputCrossProvider_RemainsNativeOnly()
    {
        var sqlServer = CoreSqlTextParser.ParseDml(
            "DELETE FROM users OUTPUT DELETED.id WHERE id = 1",
            SqlAgentToolType.MsSqlServer);

        var toPostgres = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                sqlServer,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("sqlserver-output-cross-v1")));
        Assert.Contains("native-only", toPostgres.Message, StringComparison.OrdinalIgnoreCase);

        var postgres = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);

        var toSqlServer = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                postgres,
                SqlAgentToolType.MsSqlServer,
                Context("users", DmlOperation.Delete)));
        Assert.Contains("native-only", toSqlServer.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlPlanValidationContext Context(
        string targetTable,
        DmlOperation operation) =>
        new SqlPlanValidationContext("sqlserver-output-assurance-v1")
            .WithDmlResultRowAssurance(
                DmlResultRowAssurance.NoEnabledTriggers(targetTable, operation));
}
