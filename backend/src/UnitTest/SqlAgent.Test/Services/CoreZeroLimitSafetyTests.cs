using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreZeroLimitSafetyTests
{
    [Fact]
    public void Compile_ParsedLimitZero_FailsClosedInsteadOfBecomingUnbounded()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users LIMIT 0",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("LIMIT 0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StructuredLimitZeroWithMaxRows_FailsClosedInsteadOfChangingToPolicyMax()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }],
            Limit = 0
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy(QueryMaxRows: 100)));

        Assert.Contains("LIMIT 0", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
