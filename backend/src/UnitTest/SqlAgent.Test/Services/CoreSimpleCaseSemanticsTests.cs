using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSimpleCaseSemanticsTests
{
    [Fact]
    public void Parse_SimpleCaseWithFunctionOperand_UsesFirstClassNode()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT CASE ABS(id) WHEN 1 THEN 'one' ELSE 'other' END FROM users",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        var simple = Assert.IsType<SimpleCaseExpr>(Assert.Single(select.Select).Expression);
        var comparison = Assert.IsType<BinaryExpr>(Assert.Single(simple.Branches).Condition);
        Assert.Equal("=", comparison.Operator);
        Assert.IsType<FunctionCallExpr>(comparison.Left);
    }

    [Fact]
    public void Compile_SimpleCaseFunctionOperand_IsEmittedOnce()
    {
        var command = CompileQuery(
            "SELECT CASE ABS(id) WHEN 1 THEN 'one' WHEN 2 THEN 'two' ELSE 'other' END FROM users");

        Assert.Contains("CASE ABS(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(command.Sql, "ABS(", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ABS(\"id\") =", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SimpleCaseScalarSubqueryOperand_IsEmittedOnce()
    {
        var command = CompileQuery(
            "SELECT CASE (SELECT MAX(value) FROM config) " +
            "WHEN 1 THEN 'one' WHEN 2 THEN 'two' ELSE 'other' END FROM users",
            "users",
            "config");

        Assert.Contains("CASE (SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(command.Sql, "\"config\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_DmlSimpleCaseFunctionOperand_IsEmittedOnce()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "DELETE FROM orders WHERE CASE ABS(id) " +
                "WHEN 1 THEN TRUE WHEN 2 THEN FALSE ELSE FALSE END",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());

        Assert.Contains("CASE ABS(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(command.Sql, "ABS(", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_SearchedCase_RemainsSearchedCase()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT CASE WHEN id = 1 THEN 'one' ELSE 'other' END FROM users",
            SqlAgentToolType.Postgres);
        var expression = Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression;

        Assert.IsType<CaseExpr>(expression);
        Assert.IsNotType<SimpleCaseExpr>(expression);
    }

    private static CompiledSqlCommand CompileQuery(string sql, params string[] tables) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(
                    tables.Length == 0 ? new[] { "users" } : tables,
                    StringComparer.OrdinalIgnoreCase)),
            new SqlExecutionPlanPolicy());

    private static int CountOccurrences(string value, string token, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, comparison)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
