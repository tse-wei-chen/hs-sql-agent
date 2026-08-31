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
    public void TypedQueryRuntime_CompilesPostgresLateralThroughTheRealFSharpBoundary()
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
            "SELECT q.id FROM LATERAL (SELECT id FROM public.users) q";

        var command = runtime.Compile(
            provider.Object,
            sql,
            SqlAgentToolType.Postgres,
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });

        Assert.Contains("LATERAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypedQueryRuntime_CompilesPostgresLeftOuterJoin()
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
            "SELECT u.id FROM public.users u LEFT OUTER JOIN public.orders o ON u.id = o.user_id";

        var command = runtime.Compile(
            provider.Object,
            sql,
            SqlAgentToolType.Postgres,
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users", "public.orders" });

        Assert.Contains("LEFT JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypedQueryRuntime_CompilesPostgresFullOuterJoin()
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
            "SELECT u.id FROM public.users u FULL OUTER JOIN public.orders o ON u.id = o.user_id";

        var command = runtime.Compile(
            provider.Object,
            sql,
            SqlAgentToolType.Postgres,
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users", "public.orders" });

        Assert.Contains("FULL OUTER JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void TypedQueryRuntime_CompilesRealisticPostgresCteJoinHavingQuery()
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

        const string sql = """
            WITH SystemMax AS (
                SELECT MAX(order_date) AS max_system_date
                FROM public.orders
            )
            SELECT
                c.customer_id,
                sm.max_system_date,
                MAX(o.order_date) AS last_order_date,
                (sm.max_system_date::date - MAX(o.order_date)::date) AS days_since_last_order
            FROM public.customers c
            LEFT JOIN public.orders o ON c.customer_id = o.customer_id
            CROSS JOIN SystemMax sm
            GROUP BY c.customer_id, sm.max_system_date
            HAVING
                (sm.max_system_date::date - MAX(o.order_date)::date) > 180
                OR MAX(o.order_date) IS NULL
            ORDER BY days_since_last_order DESC;
            """;

        var command = runtime.Compile(
            provider.Object,
            sql,
            SqlAgentToolType.Postgres,
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.customers",
                "public.orders"
            });

        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEFT JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAVING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 180));
    }

}
