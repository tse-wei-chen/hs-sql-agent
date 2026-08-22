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
    public void Compile_FilterAndWindow_ProducesParameterizedPostgresExpression()
    {
        var definition = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "SUM",
                    Arguments = [new FieldSelectCondition { FieldName = "amount" }],
                    FilterWhereConditions =
                    [
                        new BasicWhereCondition { FieldName = "status", Operator = "=", Value = "open" }
                    ],
                    Window = new WindowDefinition
                    {
                        PartitionBy = [new FieldGroupByCondition { FieldName = "customer_id" }],
                        OrderBy = [new FieldOrderByCondition { FieldName = "created_at", Direction = SortDirection.Asc }],
                        Frame = new WindowFrameDefinition
                        {
                            Unit = WindowFrameUnit.Rows,
                            Start = new WindowFrameBound
                            {
                                Kind = WindowFrameBoundKind.Preceding,
                                Offset = 1
                            },
                            End = new WindowFrameBound { Kind = WindowFrameBoundKind.CurrentRow }
                        }
                    }
                }
            ]
        };

        var command = CoreSqlCompiler.CreateDefault().Compile(
            definition,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OVER (PARTITION BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROWS BETWEEN 1 PRECEDING AND CURRENT ROW", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("open", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "open"));
    }

    [Fact]
    public void Compile_FilterForUnsupportedProvider_FailsClosed()
    {
        var definition = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "SUM",
                    Arguments = [new FieldSelectCondition { FieldName = "amount" }],
                    FilterWhereConditions =
                    [
                        new BasicWhereCondition { FieldName = "status", Operator = "=", Value = "open" }
                    ]
                }
            ]
        };

        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                definition,
                SqlAgentToolType.MySQL,
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("expression.filter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_SetOperationWithOuterTail_LowersAtQueryLevel(SqlAgentToolType provider)
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
            OrderByColumns =
            [
                new FieldOrderByCondition { FieldName = "id", Direction = SortDirection.Desc }
            ],
            Limit = 10,
            Offset = 5
        };

        var command = CoreSqlCompiler.CreateDefault().Compile(
            definition,
            provider,
            provider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            command.Sql.LastIndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase) >
            command.Sql.LastIndexOf("UNION", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            command.Sql.LastIndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) >
            command.Sql.LastIndexOf("UNION", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
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
