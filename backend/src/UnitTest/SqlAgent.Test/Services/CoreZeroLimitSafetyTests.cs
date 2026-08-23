using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreZeroLimitSafetyTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "LIMIT")]
    [InlineData(SqlAgentToolType.MySQL, "LIMIT")]
    [InlineData(SqlAgentToolType.Sqlite, "LIMIT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "TOP")]
    [InlineData(SqlAgentToolType.Oracle, "FETCH NEXT")]
    [InlineData(SqlAgentToolType.Firebird, "FIRST")]
    public void Compile_ParsedLimitZero_PreservesEmptyResultSemantics(
        SqlAgentToolType targetProvider,
        string expectedSqlFragment)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users LIMIT 0",
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, targetProvider);

        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.Contains(expectedSqlFragment, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, IsZeroParameter);
    }

    [Fact]
    public void Compile_StructuredLimitZeroWithMaxRows_PreservesZeroInsteadOfPolicyMax()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }],
            Limit = 0
        };
        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(QueryMaxRows: 100));

        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, IsZeroParameter);
        Assert.DoesNotContain(
            command.Parameters,
            parameter => Equals(Convert.ToInt32(parameter.Value), 100));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "LIMIT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "TOP")]
    [InlineData(SqlAgentToolType.Firebird, "FIRST")]
    public void Compile_NestedDerivedLimitZero_RemainsBounded(
        SqlAgentToolType targetProvider,
        string expectedSqlFragment)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT empty_users.id FROM (SELECT id FROM users LIMIT 0) AS empty_users",
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, targetProvider);

        Assert.Contains(expectedSqlFragment, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, IsZeroParameter);
    }

    [Fact]
    public void Compile_SetOperationTailLimitZero_IsNotDroppedByWrapperOptimization()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users UNION ALL SELECT id FROM archived_users LIMIT 0",
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, SqlAgentToolType.Postgres);

        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, IsZeroParameter);
    }

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

    private static bool IsZeroParameter(SqlParameterValue parameter) =>
        parameter.Value is not null && Convert.ToInt64(parameter.Value) == 0L;
}
