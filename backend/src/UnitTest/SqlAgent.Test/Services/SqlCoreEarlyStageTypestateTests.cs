using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Query and Mutation must retain distinct non-public stage tokens, while
/// the CLR-facing compiler and inspection contract remains unchanged.
/// </summary>
public sealed class SqlCoreEarlyStageTypestateTests
{
    [Fact]
    public void ParsedBoundCanonicalAndValidatedStages_AreDistinctAndNotPublicApi()
    {
        var assembly = typeof(SqlCoreFacade).Assembly;
        var types = assembly.GetTypes();
        foreach (var stage in new[] { "Parsed", "Bound", "Canonical", "Validated", "Authorized" })
        {
            var query = Assert.Single(types.Where(t => t.Name == stage + "Query"));
            var mutation = Assert.Single(types.Where(t => t.Name == stage + "Mutation"));
            Assert.NotEqual(query, mutation);
            Assert.False(query.IsVisible);
            Assert.False(mutation.IsVisible);
        }
        var publicFacade = typeof(SqlCoreFacade).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.DoesNotContain(publicFacade.SelectMany(m => m.GetParameters()),
            parameter => parameter.ParameterType.Name is "ParsedQuery" or "BoundQuery"
                or "CanonicalQuery" or "ValidatedQuery" or "AuthorizedQuery"
                or "ParsedMutation" or "BoundMutation" or "CanonicalMutation"
                or "ValidatedMutation" or "AuthorizedMutation");
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryFactsInspection_UsesSameCompilerOutputAcrossEveryDialect(SqlAgentToolType dialect)
    {
        const string sql = "SELECT id FROM users WHERE id = 7";
        var validation = new SqlPlanValidationContext("early-typestate-facts-v1");
        var rowCap = new SqlExecutionPlanPolicy(10);
        var direct = SqlCoreFacade.CompileQuery(sql, dialect, dialect, validation, rowCap);
        var inspected = SqlCoreFacade.CompileQueryWithFacts(sql, dialect, dialect, validation, rowCap);

        Assert.Equal(direct.Kind, inspected.Command.Kind);
        Assert.Equal(direct.Sql, inspected.Command.Sql);
        Assert.Equal(direct.PlanFingerprint, inspected.Command.PlanFingerprint);
        Assert.NotNull(inspected.Facts);
        Assert.NotNull(inspected.Command.CompileEvidence);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void MutationStages_KeepDefaultDeleteSafetyAcrossEveryDialect(SqlAgentToolType dialect)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileDml(
                "DELETE FROM users",
                dialect,
                dialect,
                new SqlPlanValidationContext("early-typestate-delete-v1")));

        var safe = SqlCoreFacade.CompileDml(
            "DELETE FROM users WHERE id = 7",
            dialect,
            dialect,
            new SqlPlanValidationContext("early-typestate-delete-v1"));

        Assert.Equal(SqlStatementKind.Delete, safe.Kind);
        Assert.Contains(safe.Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public void QueryRowCap_StillAppliesToInspectionAndDirectCompilation()
    {
        var validation = new SqlPlanValidationContext("early-typestate-cap-v1");
        var policy = new SqlExecutionPlanPolicy(3);
        const string sql = "SELECT id FROM users ORDER BY id";
        var direct = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, validation, policy);
        var inspected = SqlCoreFacade.CompileQueryWithFacts(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, validation, policy);

        Assert.Contains("LIMIT", direct.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(direct.Parameters, parameter => Equals(parameter.Value, 3));
        Assert.Equal(direct.Sql, inspected.Command.Sql);
    }
}
