using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class PostgresSyntaxBoundaryTests
{
    [Fact]
    public void TypedQueryRuntime_CompilesCteThroughTheRealFSharpBoundary()
    {
        var runtime = new TypedQueryRuntime();
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);

        var policy = new SecurityPolicyModel
        {
            QueryMaxRows = 100,
            QueryTimeoutSeconds = 30,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true,
            AllowFullTableUpdate = false,
            AllowFullTableDelete = false,
            DmlMaxAffectedRows = 100
        };

        const string sql =
            "WITH recent AS (SELECT id FROM public.users WHERE status = 'active') SELECT id FROM recent";

        var command = runtime.Compile(
            provider.Object,
            sql,
            SqlAgentToolType.Postgres,
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });

        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "active"));
    }

    [Fact]
    public void TypedQueryRuntime_CompilesWildcardCteWithoutTreatingAliasAsPhysicalTable()
    {
        var runtime = new TypedQueryRuntime();
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);

        var policy = new SecurityPolicyModel
        {
            QueryMaxRows = 100,
            QueryTimeoutSeconds = 30,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true,
            AllowFullTableUpdate = false,
            AllowFullTableDelete = false,
            DmlMaxAffectedRows = 100
        };

        const string sql =
            "WITH recent AS (SELECT * FROM public.users) SELECT * FROM recent";

        var command = runtime.Compile(
            provider.Object,
            sql,
            SqlAgentToolType.Postgres,
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });

        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public", command.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
