using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreCompilerBoundaryTests
{
    [Fact]
    public void MapAndCompile_LegacyEquivalentSpellings_DoNotMutateTransportDto()
    {
        var token = new TemplateSqlTokenSelectCondition
        {
            Token = "CURRENT_TIMESTAMP",
            Alias = "compiled_at"
        };
        var predicate = new BasicWhereCondition
        {
            FieldName = "deleted_at",
            Operator = "ISNULL",
            Value = null
        };
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [token],
            WhereColumnsAndValues = [predicate]
        };

        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);
        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal("ISNULL", predicate.Operator);
        Assert.Same(token, definition.SelectColumns![0]);
        Assert.Equal("CURRENT_TIMESTAMP", token.Token);
        Assert.Contains("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ParsedStatement_IsTheTypedCompilerEntryPoint()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
        };
        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new SqlExecutionPlanPolicy(QueryMaxRows: 10));

        Assert.Contains("users", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }
}
