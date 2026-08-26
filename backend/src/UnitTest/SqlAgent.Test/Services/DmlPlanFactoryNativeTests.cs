using Moq;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlPlanFactoryNativeTests
{
    [Fact]
    public async Task CreateAsync_UsesSameCorePredicateForMutationAndMatchPlan()
    {
        var metadata = CreateUsersMetadata(primaryKey: true);
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET status = 'disabled' WHERE id = 7 AND owner_id = other_id",
            SqlAgentToolType.Postgres);

        var plan = await new DmlPlanFactory(metadata.Object).CreateAsync(
            "connection",
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy(),
            DmlRowIdentityAssurance.Strict,
            maxAffectedRows: 25,
            cancellationToken: TestContext.Current.CancellationToken);
        var matchCommand = Assert.IsType<CompiledSqlCommand>(plan.MatchQueryCommand);

        Assert.Equal(DmlApprovalMode.RowSetMutation, plan.ApprovalMode);
        Assert.Equal("public.users", plan.TableName);
        Assert.Equal("id", Assert.Single(plan.RowIdentityColumns));
        Assert.Equal(SqlStatementKind.Update, plan.MutationCommand.Kind);
        Assert.Equal(SqlStatementKind.Select, matchCommand.Kind);
        Assert.Contains("owner_id", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other_id", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owner_id", matchCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other_id", matchCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.MutationCommand.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(plan.MutationCommand.Parameters, parameter => Equals(parameter.Value, 7));
        Assert.Single(matchCommand.Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public async Task CreateAsync_InsertValues_BindsExactPayloadWithoutPrimaryKeyMatchPlan()
    {
        var metadata = CreateUsersMetadata(primaryKey: false);
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, status) VALUES (7, 'active'), (8, 'pending')",
            SqlAgentToolType.Postgres);

        var plan = await new DmlPlanFactory(metadata.Object).CreateAsync(
            "connection",
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-insert"),
            maxAffectedRows: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DmlOperation.Insert, plan.Operation);
        Assert.Equal(DmlApprovalMode.InsertValues, plan.ApprovalMode);
        Assert.Equal("public.users", plan.TableName);
        Assert.Equal(SqlStatementKind.Insert, plan.MutationCommand.Kind);
        Assert.Null(plan.MatchQueryCommand);
        Assert.Empty(plan.RowIdentityColumns);
        Assert.Equal(DmlRowIdentityAssurance.CountOnly, plan.RowIdentityAssurance);
        Assert.Equal(2, plan.InsertRows.Length);
        Assert.Equal(7L, Convert.ToInt64(plan.InsertRows[0]["id"]));
        Assert.Equal("active", plan.InsertRows[0]["status"]);
        Assert.Equal(8L, Convert.ToInt64(plan.InsertRows[1]["id"]));
        Assert.Equal("pending", plan.InsertRows[1]["status"]);
        Assert.Equal(
            DmlFingerprintService.ComputePlanFingerprint(plan.MutationCommand, "policy-insert"),
            plan.PlanFingerprint);
    }

    [Fact]
    public async Task CreateAsync_InsertValues_RejectsPayloadAboveMaximumBeforeApproval()
    {
        var metadata = CreateUsersMetadata(primaryKey: false);
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, status) VALUES (7, 'active'), (8, 'pending')",
            SqlAgentToolType.Postgres);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new DmlPlanFactory(metadata.Object).CreateAsync(
                "connection",
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-insert"),
                maxAffectedRows: 1,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("rowCount=2", error.Message, StringComparison.Ordinal);
    }

    private static Mock<IProviderMetadataReader> CreateUsersMetadata(bool primaryKey)
    {
        var metadata = new Mock<IProviderMetadataReader>();
        metadata
            .Setup(x => x.GetSchemasAsync("connection", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["public"]);
        metadata
            .Setup(x => x.GetTablesAsync("connection", "public", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["users"]);
        metadata
            .Setup(x => x.GetColumnsAsync(
                "connection",
                "public",
                "users",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseColumnMetadata("public", "users", "id", "integer", primaryKey, primaryKey ? 1 : null),
                new DatabaseColumnMetadata("public", "users", "owner_id", "integer", false),
                new DatabaseColumnMetadata("public", "users", "status", "text", false)
            ]);
        return metadata;
    }
}
