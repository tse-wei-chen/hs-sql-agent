using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlCompilerBoundaryTests
{
    [Fact]
    public void Compile_ParsedUpdate_ProducesTypedCommand()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Update,
            TableName = "public.users",
            Values = [new NameValuePair { FieldName = "status", Value = "disabled" }],
            WhereConditions =
            [
                new BasicWhereCondition { FieldName = "id", Operator = "=", Value = 7 }
            ]
        };
        var parsed = new ParsedStatement(
            DmlDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public void Compile_ParsedUpdateWithoutPredicate_IsDeniedByCorePolicy()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Update,
            TableName = "public.users",
            Values = [new NameValuePair { FieldName = "status", Value = "disabled" }]
        };
        var parsed = new ParsedStatement(
            DmlDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);

        Assert.Throws<UnauthorizedAccessException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));
    }

    [Fact]
    public void Compile_QueryParsedStatement_IsRejectedByDmlCompiler()
    {
        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(new QueryDefinition
            {
                TableName = "public.users",
                SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
            }),
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("Unsupported DML statement", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
