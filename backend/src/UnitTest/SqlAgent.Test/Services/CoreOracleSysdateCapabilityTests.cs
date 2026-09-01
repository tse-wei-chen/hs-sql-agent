using HsSqlAgent.SqlCore;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreOracleSysdateCapabilityTests
{
    [Fact]
    public void Parse_OracleBareSysdate_IsStructuredAsZeroArgumentFunction()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT SYSDATE FROM dual",
            SqlAgentToolType.Oracle);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var function = Assert.IsType<FunctionCallExpr>(
            Assert.Single(select.Select).Expression);

        Assert.Equal("SYSDATE", IdentifierText(function.Name), ignoreCase: true);
        Assert.Empty(function.Arguments);
    }

    [Fact]
    public void Compile_OracleBareSysdate_PreservesNativeServerClockSemantics()
    {
        var command = Compile(
            "SELECT SYSDATE FROM dual",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Oracle);

        Assert.Contains("SYSDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYSDATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_OracleSysdateAcrossProviders_FailsInsteadOfRenamingClockSemantics(
        SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT SYSDATE FROM dual",
                SqlAgentToolType.Oracle,
                targetProvider));

        Assert.Contains(
            "function.oracle_sysdate",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "native-only",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_OracleSysdateWithParentheses_RemainsRejected()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT SYSDATE() FROM dual",
                SqlAgentToolType.Oracle));

        Assert.Contains("SYSDATE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parentheses", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NonOracleBareSysdate_DoesNotAcquireOracleSemantics()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT SYSDATE FROM users",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.IsType<ColumnExpr>(Assert.Single(select.Select).Expression);
    }

    [Fact]
    public void Matrix_OracleSysdate_IsOracleOnly()
    {
        Assert.Equal(
            SqlCapabilityStatus.Supported,
            Capability(SqlAgentToolType.Oracle).Status);

        foreach (var provider in new[]
        {
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Firebird
        })
        {
            Assert.Equal(
                SqlCapabilityStatus.Rejected,
                Capability(provider).Status);
        }
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join(".", identifier.Parts.Select(part => part.Value));

    private static SqlCapability Capability(SqlAgentToolType provider) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "function.oracle_sysdate");

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(
            sql,
            source,
            target,
            new SqlPlanValidationContext("oracle-sysdate-v1"),
            new SqlExecutionPlanPolicy());
}
