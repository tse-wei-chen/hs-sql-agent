using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlCompilerTests
{
    [Fact]
    public void Compile_BasicQuery_ProducesParameterizedImmutableCommand()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns = [new FieldSelectCondition { FieldName = "u.id" }],
            WhereColumnsAndValues =
            [
                new BasicWhereCondition { FieldName = "u.name", Operator = "=", Value = "alice" }
            ]
        };

        var command = CoreSqlCompiler.CreateDefault().Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new SqlExecutionPlanPolicy(QueryMaxRows: 50));

        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.Equal(SqlAgentToolType.Postgres, command.TargetProvider);
        Assert.DoesNotContain("alice", command.Sql, StringComparison.Ordinal);
        Assert.Contains("@p0", command.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("alice", command.Parameters[0].Value);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 50));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_CrossDialectFunction_UsesCanonicalTargetName()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "LEN",
                    Arguments = [new FieldSelectCondition { FieldName = "name" }]
                }
            ]
        };

        var command = CoreSqlCompiler.CreateDefault().Compile(
            definition,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("LENGTH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LEN(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationWithOuterLimit_FailsClosedForCurrentBackend()
    {
        var definition = new QueryDefinition
        {
            TableName = "active_users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }],
            CombineConditions =
            [
                new CombineCondition
                {
                    Query = new QueryDefinition
                    {
                        TableName = "archived_users",
                        SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
                    }
                }
            ],
            Limit = 10
        };

        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("set operation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserve semantics", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_WhitelistViolation_CannotReachLowering()
    {
        var definition = new QueryDefinition
        {
            TableName = "secrets",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext(
                    "policy-v1",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
                new SqlExecutionPlanPolicy()));
    }
}
