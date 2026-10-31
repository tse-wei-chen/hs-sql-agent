using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreProvenValidationFastPathTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void FullyProvenFilterAndJoinChecksStillCompileDeepQueries(SqlAgentToolType provider)
    {
        // The ordinary non-Regex Query graph exercises the same per-source
        // Filter and Join proof transitions as the 48-case benchmark.
        var sql = "SELECT id FROM users WHERE id = 11";
        for (var depth = 0; depth < 32; depth++)
            sql = $"SELECT d{depth}.id FROM ({sql}) d{depth}";
        var result = SqlCoreFacade.TryCompileQuery(
            sql, provider, provider,
            new SqlPlanValidationContext("proven-filter-join-depth-v1"),
            new SqlExecutionPlanPolicy(100));

        Assert.True(result.Success, $"{provider}: {result.ErrorCode} {result.ErrorMessage}");
        Assert.Empty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ProvenSourceFilterDoesNotGrantRejectedTargetIlike(SqlAgentToolType target)
    {
        var result = SqlCoreFacade.TryCompileQuery(
            "SELECT name FROM users WHERE name ILIKE 'a%'",
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("proven-filter-target-reject-v1"),
            new SqlExecutionPlanPolicy(100));
        Assert.False(result.Success);
        Assert.Contains("operator.ilike", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MySQL)]
    public void ProvenSourceJoinsNeverGrantRejectedTargetFullJoin(SqlAgentToolType target)
    {
        var result = SqlCoreFacade.TryCompileQuery(
            "SELECT u.id FROM users u FULL OUTER JOIN orders o ON u.id = o.id",
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("proven-join-target-reject-v1"),
            new SqlExecutionPlanPolicy(100));
        Assert.False(result.Success);
        Assert.Contains("join.full", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProvenSourceJoinStillRendersValidPostgresJoin()
    {
        var result = SqlCoreFacade.TryCompileQuery(
            "SELECT u.id FROM users u FULL OUTER JOIN orders o ON u.id = o.id",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("proven-join-positive-v1"),
            new SqlExecutionPlanPolicy(100));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.TypedDiagnostics);
    }
}
