using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlCompilerTests
{
    [Fact]
    public void Compile_Update_ProducesParameterizedCommand()
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

        var command = CoreDmlCompiler.CreateDefault().Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" 7", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 7));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_Delete_ProducesParameterizedCommand()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Delete,
            TableName = "public.users",
            WhereConditions =
            [
                new BasicWhereCondition { FieldName = "id", Operator = "=", Value = 7 }
            ]
        };

        var command = CoreDmlCompiler.CreateDefault().Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.Contains("DELETE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public void Compile_UpdateWithoutWhere_IsDeniedByDefault()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Update,
            TableName = "public.users",
            Values = [new NameValuePair { FieldName = "status", Value = "disabled" }]
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));
    }

    [Fact]
    public void Compile_WhitelistViolation_IsDeniedBeforeLowering()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Delete,
            TableName = "public.secrets",
            WhereConditions =
            [
                new BasicWhereCondition { FieldName = "id", Operator = "=", Value = 1 }
            ]
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext(
                    "policy-v1",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" })));
    }

    [Fact]
    public void Compile_Insert_RemainsFailClosed()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Insert,
            TableName = "public.users",
            Values = [new NameValuePair { FieldName = "status", Value = "active" }]
        };

        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("INSERT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
