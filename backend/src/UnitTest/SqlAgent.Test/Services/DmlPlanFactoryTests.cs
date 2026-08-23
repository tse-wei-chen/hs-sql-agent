using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlPlanFactoryTests
{
    [Fact]
    public async Task CreateAsync_Strict_BuildsMutationAndPrimaryKeyMatchFromSameDefinition()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
            new DatabaseColumnMetadata("public", "users", "status", "text", false)
        ]);
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
            Map(definition),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v2",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }),
            new DmlCompilationPolicy(),
            DmlRowIdentityAssurance.Strict,
            maxAffectedRows: 25,
            approvalTtl: TimeSpan.FromMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["id"], plan.RowIdentityColumns);
        Assert.Equal(DmlRowIdentityAssurance.Strict, plan.RowIdentityAssurance);
        Assert.Equal(SqlStatementKind.Update, plan.MutationCommand.Kind);
        Assert.Equal(SqlStatementKind.Select, plan.MatchQueryCommand.Kind);
        Assert.Contains("UPDATE", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", plan.MutationCommand.Sql, StringComparison.Ordinal);
        Assert.Contains(plan.MutationCommand.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(plan.MutationCommand.Parameters, parameter => Equals(parameter.Value, 7));
        Assert.Contains("id", plan.MatchQueryCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7", plan.MatchQueryCommand.Sql, StringComparison.Ordinal);
        Assert.Contains(plan.MatchQueryCommand.Parameters, parameter => Equals(parameter.Value, 7));
        Assert.Contains(plan.MatchQueryCommand.Parameters, parameter => Equals(parameter.Value, 26));
        Assert.Equal("policy-v2", plan.PolicyVersion);
        Assert.Equal(TimeSpan.FromMinutes(2), plan.ApprovalTtl);
        Assert.Equal(25, plan.MaxAffectedRows);
        Assert.Equal(
            DmlFingerprintService.ComputePlanFingerprint(plan.MutationCommand, "policy-v2"),
            plan.PlanFingerprint);
    }

    [Fact]
    public async Task CreateAsync_UnqualifiedTarget_UsesResolvedQualifiedTableForBothCommands()
    {
        var metadata = new StubMetadataReader(
        [
            new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
            new DatabaseColumnMetadata("public", "users", "status", "text", false)
        ],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["audit"] = ["events"],
            ["public"] = ["users"]
        });
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Update,
            TableName = "users",
            Values = [new NameValuePair { FieldName = "status", Value = "disabled" }],
            WhereConditions =
            [
                new BasicWhereCondition { FieldName = "id", Operator = "=", Value = 7 }
            ]
        };

        var plan = await new DmlPlanFactory(metadata).CreateAsync(
            "connection",
            Map(definition),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "policy-v3",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("public.users", plan.TableName);
        Assert.Contains("public", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public", plan.MatchQueryCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", plan.MatchQueryCommand.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_Strict_RejectsMissingPrimaryKey()
    {
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Delete,
            TableName = "public.events",
            WhereConditions =
            [
                new BasicWhereCondition { FieldName = "status", Operator = "=", Value = "old" }
            ]
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlPlanFactory(new StubMetadataReader(
            [
                new DatabaseColumnMetadata("public", "events", "status", "text", false)
            ])).CreateAsync(
                "connection",
                Map(definition),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                assurance: DmlRowIdentityAssurance.Strict,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("primary key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedStatement Map(DmlDefinition definition) =>
        new(DmlDefinitionCoreMapper.Map(definition), SqlAgentToolType.Postgres);

    private sealed class StubMetadataReader(
        IReadOnlyList<DatabaseColumnMetadata> columns,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? tablesBySchema = null)
        : IProviderMetadataReader
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _tablesBySchema =
            tablesBySchema ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<string>> GetSchemasAsync(
            string connectionString,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_tablesBySchema.Keys.ToArray());

        public Task<IReadOnlyList<string>> GetTablesAsync(
            string connectionString,
            string schema,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _tablesBySchema.TryGetValue(schema, out var tables)
                    ? tables
                    : (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
            string connectionString,
            string schema,
            string table,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(columns);
    }
}
