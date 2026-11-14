using Admin.Service.Models;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class SqlExplainCompilerTests
{
    [Fact]
    public void ExplainQuery_ReturnsRenderedSqlFactsParametersAndEvidence()
    {
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var request = new SqlExplainRequest
        {
            DbManagementId = 1,
            StatementType = "Query",
            Sql = "SELECT id FROM public.users WHERE status = 'active'"
        };

        var result = new SqlExplainCompiler().Explain(
            provider.Object,
            request,
            SqlAgentToolType.Postgres,
            CreatePolicy(maxRows: 25),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" },
            new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres, new Version(17, 0)),
            GlobalScope("execute_query_sql"));

        Assert.True(result.Success);
        Assert.False(result.Executed);
        Assert.Equal("compile-only", result.SimulationMode);
        var statement = Assert.Single(result.Statements);
        Assert.Contains("LIMIT", statement.RenderedSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", statement.RenderedSql, StringComparison.Ordinal);
        Assert.Contains(statement.Parameters, parameter => Equals(parameter.Value, "active"));
        Assert.NotNull(statement.QueryFacts);
        Assert.Contains("public.users", statement.QueryFacts!.ReferencedTables, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(statement.Evidence);
        Assert.Equal("Translated", statement.Evidence!.Verdict);
        Assert.Equal("Completed", statement.Evidence.DecisionBoundary);
    }

    [Fact]
    public void ExplainQuery_TableScopeViolation_FailsClosedWithPolicyEvidence()
    {
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var request = new SqlExplainRequest
        {
            DbManagementId = 1,
            StatementType = "Query",
            Sql = "SELECT id FROM public.secrets"
        };

        var result = new SqlExplainCompiler().Explain(
            provider.Object,
            request,
            SqlAgentToolType.Postgres,
            CreatePolicy(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" },
            new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres, new Version(17, 0)),
            GlobalScope("execute_query_sql"));

        Assert.False(result.Success);
        Assert.Empty(result.Statements);
        Assert.NotNull(result.Failure);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure!.Code));
        Assert.True(
            string.Equals(result.Failure.Stage, "Policy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Failure.Stage, "Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplainDml_FullTableUpdate_ReflectsCurrentPolicyRejection()
    {
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var request = new SqlExplainRequest
        {
            DbManagementId = 1,
            StatementType = "DML",
            Sql = "UPDATE public.users SET name = 'Ada'"
        };

        var result = new SqlExplainCompiler().Explain(
            provider.Object,
            request,
            SqlAgentToolType.Postgres,
            CreatePolicy(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" },
            new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres, new Version(17, 0)),
            GlobalScope("execute_dml_sql"));

        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Equal("Policy", result.Failure!.Stage);
        Assert.NotNull(result.Failure.Evidence);
        Assert.Equal("Rejected", result.Failure.Evidence!.Verdict);
        Assert.False(result.Policy.DmlAffectedRowLimitEvaluated);
    }

    [Fact]
    public void ExplainDml_MultiStatementBatch_ReturnsEachRenderedStatementWithoutExecution()
    {
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var request = new SqlExplainRequest
        {
            DbManagementId = 1,
            StatementType = "DML",
            Sql = "UPDATE public.users SET name = 'Ada' WHERE id = 1; DELETE FROM public.sessions WHERE user_id = 1"
        };

        var result = new SqlExplainCompiler().Explain(
            provider.Object,
            request,
            SqlAgentToolType.Postgres,
            CreatePolicy(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users", "public.sessions" },
            new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres, new Version(17, 0)),
            GlobalScope("execute_dml_sql"));

        Assert.True(result.Success);
        Assert.False(result.Executed);
        Assert.Equal(2, result.Statements.Count);
        Assert.All(result.Statements, statement => Assert.NotNull(statement.Evidence));
        Assert.Contains(result.Statements[0].Parameters, parameter => Equals(parameter.Value, "Ada"));
    }

    [Fact]
    public void Denied_DoesNotPretendCompilerRan()
    {
        var request = new SqlExplainRequest
        {
            DbManagementId = 1,
            StatementType = "DML",
            Sql = "DELETE FROM public.users WHERE id = 1",
            AccessKeyId = 9
        };
        var scope = new SqlExplainScope(
            9,
            "read-only",
            true,
            "execute_dml_sql",
            false,
            "4 selected tools",
            "All tables",
            []);

        var result = SqlExplainCompiler.Denied(
            request,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            CreatePolicy(),
            scope,
            "authorization.tool_denied",
            "DML is not allowed.");

        Assert.False(result.Success);
        Assert.False(result.Executed);
        Assert.Empty(result.Statements);
        Assert.Equal("Authorization", result.Failure!.Stage);
        Assert.Null(result.Failure.Evidence);
    }

    private static Mock<ISqlProvider> CreateProvider(SqlAgentToolType type)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(value => value.Type).Returns(type);
        return provider;
    }

    private static SecurityPolicyModel CreatePolicy(int maxRows = 100) => new()
    {
        QueryMaxRows = maxRows,
        QueryTimeoutSeconds = 30,
        RequireWhereForUpdate = true,
        RequireWhereForDelete = true,
        AllowFullTableUpdate = false,
        AllowFullTableDelete = false,
        DmlMaxAffectedRows = 100,
        KeyPermitLimit = 120,
        KeyWindowSeconds = 60,
        MaxConcurrentSql = 16
    };

    private static SqlExplainScope GlobalScope(string tool) =>
        new(
            null,
            null,
            true,
            tool,
            true,
            "Global policy only",
            "All tables",
            []);
}
