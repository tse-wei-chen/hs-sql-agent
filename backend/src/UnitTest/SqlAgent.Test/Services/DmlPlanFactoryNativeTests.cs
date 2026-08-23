using Moq;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlPlanFactoryNativeTests
{
    [Fact]
    public async Task CreateAsync_UsesSameCorePredicateForMutationAndMatchPlan()
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
                new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
                new DatabaseColumnMetadata("public", "users", "owner_id", "integer", false),
                new DatabaseColumnMetadata("public", "users", "status", "text", false)
            ]);

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

        Assert.Equal("public.users", plan.TableName);
        Assert.Equal(["id"], plan.IdentityColumns);
        Assert.Equal(SqlStatementKind.Update, plan.MutationCommand.Kind);
        Assert.Equal(SqlStatementKind.Select, plan.MatchCommand.Kind);
        Assert.Contains("owner_id", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other_id", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owner_id", plan.MatchCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other_id", plan.MatchCommand.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.MutationCommand.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(plan.MutationCommand.Parameters, parameter => Equals(parameter.Value, 7));
        Assert.Single(plan.MatchCommand.Parameters, parameter => Equals(parameter.Value, 7));
    }
}
