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
    public void Compile_InsertSingleRow_ProducesParameterizedCommand()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Insert,
            TableName = "public.users",
            Values =
            [
                new NameValuePair { FieldName = "name", Value = "Alice" },
                new NameValuePair { FieldName = "age", Value = 30 }
            ]
        };

        var command = CoreDmlCompiler.CreateDefault().Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }));

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("INSERT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "Alice"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 30));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_InsertMultiRow_PreservesOrderedBindings()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Insert,
            TableName = "public.users",
            Columns = ["name", "age"],
            MultiValues =
            [
                ["Alice", 30],
                ["Bob", 40]
            ]
        };

        var command = CoreDmlCompiler.CreateDefault().Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Equal(["Alice", 30, "Bob", 40], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void Compile_InsertRejectsConflictingSources()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Insert,
            TableName = "public.users",
            Values = [new NameValuePair { FieldName = "name", Value = "Alice" }],
            Columns = ["name"],
            MultiValues = [["Bob"]]
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("exactly one source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelect_AuthorizesSourceBeforeBackendFailClosed()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Insert,
            TableName = "public.archive",
            Columns = ["id"],
            FromQuery = new QueryDefinition
            {
                TableName = "public.users",
                SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
            }
        };
        var compiler = CoreDmlCompiler.CreateDefault();

        Assert.Throws<UnauthorizedAccessException>(() => compiler.Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.archive" })));

        var backendError = Assert.Throws<SqlCompilationException>(() => compiler.Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "public.archive",
                    "public.users"
                })));
        Assert.Contains("INSERT..SELECT", backendError.Message, StringComparison.OrdinalIgnoreCase);
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
}
