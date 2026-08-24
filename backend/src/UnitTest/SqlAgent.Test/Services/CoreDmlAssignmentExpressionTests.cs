using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlAssignmentExpressionTests
{
    [Fact]
    public void ParseUpdate_CurrentDate_IsModeledAsCurrentTemporalFunction()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE orders SET order_date = CURRENT_DATE, required_date = CURRENT_DATE WHERE order_id = 11077",
            SqlAgentToolType.Postgres);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);

        Assert.Equal(2, update.Assignments.Length);
        Assert.All(update.Assignments, assignment =>
        {
            var function = Assert.IsType<FunctionCallExpr>(assignment.Value);
            Assert.Equal("CURRENT_DATE", Assert.Single(function.Name.Parts).Value, ignoreCase: true);
            Assert.Empty(function.Arguments);
            Assert.False(function.IsDistinct);
        });
    }

    [Fact]
    public void CompileUpdate_CurrentDate_UsesClosedSqlExpressionAndKeepsPredicateParameterized()
    {
        var command = Compile(
            "UPDATE orders SET order_date = CURRENT_DATE, required_date = CURRENT_DATE WHERE order_id = 11077",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("CURRENT_DATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal(11077, parameter.Value);
    }

    [Fact]
    public void CompileUpdate_CurrentDate_UsesSqlServerDateSemanticsWhenTargetChanges()
    {
        var command = Compile(
            "UPDATE orders SET order_date = CURRENT_DATE WHERE order_id = 11077",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("CAST(CURRENT_TIMESTAMP AS date)", command.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal(11077, parameter.Value);
    }

    [Fact]
    public void ParseAndCompileUpdate_PostgresDateCast_BecomesTypedParameterizedLiteral()
    {
        const string sql = "UPDATE orders SET order_date = '2026-08-23'::date, required_date = '2026-08-23'::date WHERE order_id = 11077";
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);

        Assert.All(update.Assignments, assignment =>
        {
            var literal = Assert.IsType<LiteralExpr>(assignment.Value);
            var date = Assert.IsType<SqlDateValue>(literal.Value);
            Assert.Equal(new DateOnly(2026, 8, 23), date.Value);
        });

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.DoesNotContain("2026-08-23", command.Sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Count(parameter =>
            parameter.Value is DateTime dateTime && dateTime == new DateTime(2026, 8, 23)));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 11077));
    }

    [Fact]
    public void CompileUpdate_MultipleLiteralAssignments_PreservesBindingOrder()
    {
        var command = Compile(
            "UPDATE orders SET status = 'open', quantity = 2 WHERE order_id = 11077",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.DoesNotContain("open", command.Sql, StringComparison.Ordinal);
        Assert.Equal(
            new object?[] { "open", 2, 11077 },
            command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void ParseUpdate_ArbitraryAssignmentExpression_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "UPDATE orders SET required_date = required_date + 1 WHERE order_id = 11077",
                SqlAgentToolType.Postgres));

        Assert.Contains("UPDATE assignment values support", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseInsert_CurrentDate_RemainsLiteralOnly()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO orders(order_date) VALUES (CURRENT_DATE)",
                SqlAgentToolType.Postgres));

        Assert.Contains("Only scalar literals are accepted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"));
}
