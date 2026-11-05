using Xunit;

namespace SqlAgent.Test.Services;

public class CoreCompilerBoundaryTests
{
    [Fact]
    public void MapAndCompile_LegacyPredicateSpelling_DoesNotMutateTransportDto()
    {
        var select = new FieldSelectCondition { FieldName = "id" };
        var predicate = new BasicWhereCondition
        {
            FieldName = "deleted_at",
            Operator = "ISNULL",
            Value = null
        };
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [select],
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
        Assert.Same(select, definition.SelectColumns![0]);
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
            new SqlExecutionPlanPolicy(10));

        Assert.Contains("users", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }
}
