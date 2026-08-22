using System.Collections.Immutable;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlPlanFactoryTests
{
    [Fact]
    public async Task CreateAsync_Strict_BuildsParameterizedPrimaryKeyMatchCommand()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
            new DatabaseColumnMetadata("public", "users", "status", "text", false)
        ]);
        var mutation = new CompiledSqlCommand(
            "UPDATE \"public\".\"users\" SET \"status\" = @p0 WHERE \"id\" = @p1",
            [new SqlParameterValue("@p0", "disabled"), new SqlParameterValue("@p1", 7)],
            SqlStatementKind.Update,
            string.Empty,
            SqlAgentToolType.Postgres);
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

        var plan = await new DmlPlanFactory(metadata).CreateAsync(
            "connection",
            definition,
            mutation,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v2",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }),
            DmlRowIdentityAssurance.Strict,
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(["id"], plan.RowIdentityColumns);
        Assert.Equal(DmlRowIdentityAssurance.Strict, plan.RowIdentityAssurance);
        Assert.Equal(SqlStatementKind.Select, plan.MatchQueryCommand.Kind);
        Assert.Contains("id", plan.MatchQueryCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7", plan.MatchQueryCommand.Sql, StringComparison.Ordinal);
        Assert.Contains(plan.MatchQueryCommand.Parameters, parameter => Equals(parameter.Value, 7));
        Assert.Equal("policy-v2", plan.PolicyVersion);
        Assert.Equal(TimeSpan.FromMinutes(2), plan.ApprovalTtl);
        Assert.Equal(
            DmlFingerprintService.ComputePlanFingerprint(mutation, "policy-v2"),
            plan.PlanFingerprint);
    }

    [Fact]
    public async Task CreateAsync_RejectsMutationKindMismatch()
    {
        var mutation = new CompiledSqlCommand(
            "DELETE FROM users",
            ImmutableArray<SqlParameterValue>.Empty,
            SqlStatementKind.Delete,
            string.Empty,
            SqlAgentToolType.Postgres);
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Update,
            TableName = "public.users"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlPlanFactory(new StubMetadataReader([])).CreateAsync(
                "connection",
                definition,
                mutation,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class StubMetadataReader(IReadOnlyList<DatabaseColumnMetadata> columns)
        : IProviderMetadataReader
    {
        public Task<IReadOnlyList<string>> GetSchemasAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetTablesAsync(
            string connectionString,
            string schema,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(columns);
    }
}
