using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DialectSyntaxBoundaryMatrixTests
{
    private sealed record DialectSpec(
        SqlAgentToolType Dialect,
        string NativeSql,
        string NativeRenderedFragments,
        string NativeTables);

    private static readonly DialectSpec[] Dialects =
    [
        new(
            SqlAgentToolType.Postgres,
            "WITH recent AS (SELECT id::bigint AS id FROM orders WHERE name ILIKE 'a%') SELECT id FROM recent",
            "WITH;CAST(;ILIKE",
            "orders"),
        new(
            SqlAgentToolType.MySQL,
            "WITH recent(id) AS (SELECT id FROM orders JOIN archive_orders USING (id)) SELECT id FROM recent ORDER BY id LIMIT 2, 10",
            "WITH;USING;LIMIT;OFFSET",
            "orders,archive_orders"),
        new(
            SqlAgentToolType.MsSqlServer,
            "WITH recent AS (SELECT [id] AS id FROM [orders]) SELECT id FROM recent ORDER BY id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            "WITH;ROW_NUMBER();[_core_page_row]",
            "orders"),
        new(
            SqlAgentToolType.Sqlite,
            "WITH recent AS (SELECT GROUP_CONCAT(name) AS id FROM users) SELECT id FROM recent ORDER BY id LIMIT 10 OFFSET 2",
            "WITH;GROUP_CONCAT(;LIMIT;OFFSET",
            "users"),
        new(
            SqlAgentToolType.Oracle,
            "WITH recent AS (SELECT SYSDATE AS id FROM dual) SELECT id FROM recent ORDER BY id FETCH FIRST 10 ROWS ONLY",
            "WITH;SYSDATE;FETCH NEXT",
            "dual"),
        new(
            SqlAgentToolType.Firebird,
            "WITH recent AS (SELECT TIMESTAMP '2026-08-24 12:34:56' AS id FROM events) SELECT id FROM recent ORDER BY id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            "WITH;ROWS; TO ",
            "events")
    ];

    public static IEnumerable<object[]> SixDialectBoundaryMatrix()
    {
        foreach (var spec in Dialects)
        {
            yield return Case(
                spec.Dialect,
                "cte-basic",
                "WITH recent AS (SELECT id FROM orders WHERE amount > 10) SELECT id FROM recent",
                "WITH",
                "orders");

            yield return Case(
                spec.Dialect,
                "cte-physical-join",
                "WITH recent AS (SELECT id FROM orders) SELECT recent.id FROM recent JOIN users u ON recent.id = u.id",
                "WITH;JOIN",
                "orders,users");

            yield return Case(
                spec.Dialect,
                "dialect-native",
                spec.NativeSql,
                spec.NativeRenderedFragments,
                spec.NativeTables);
        }
    }

    [Fact]
    public void SixDialectBoundaryMatrix_HasStableCoverage()
    {
        var cases = SixDialectBoundaryMatrix().ToArray();

        Assert.Equal(18, cases.Length);
        Assert.Equal(
            18,
            cases
                .Select(item => $"{item[0]}::{item[1]}")
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            Assert.Equal(
                3,
                cases.Count(item => Equals(item[0], dialect)));
        }
    }

    [Theory]
    [MemberData(nameof(SixDialectBoundaryMatrix))]
    public void TypedQueryRuntime_CompilesSixDialectGrammarThroughRealFSharpBoundary(
        SqlAgentToolType dialect,
        string scenario,
        string sql,
        string expectedRenderedFragments,
        string allowedTablesCsv)
    {
        var runtime = new TypedQueryRuntime();
        var provider = SyntaxBoundaryTestSupport.Provider(dialect);

        var command = runtime.Compile(
            provider.Object,
            sql,
            dialect,
            SyntaxBoundaryTestSupport.Policy(),
            SyntaxBoundaryTestSupport.AllowedTables(allowedTablesCsv));

        Assert.Equal(dialect, command.TargetProvider);
        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql), scenario);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint), scenario);

        foreach (var fragment in expectedRenderedFragments.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.Contains(
                fragment,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }

        if (scenario == "cte-basic")
        {
            Assert.DoesNotContain("10", command.Sql, StringComparison.Ordinal);
            Assert.Contains(
                command.Parameters,
                parameter => SyntaxIntegerParameter(parameter.Value, 10L));
        }

        if (scenario == "dialect-native" && dialect == SqlAgentToolType.Postgres)
        {
            Assert.DoesNotContain("a%", command.Sql, StringComparison.Ordinal);
            Assert.Contains(
                command.Parameters,
                parameter => Equals(parameter.Value, "a%"));
        }

        if (scenario == "dialect-native" && dialect == SqlAgentToolType.Firebird)
        {
            Assert.DoesNotContain("2026-08-24 12:34:56", command.Sql, StringComparison.Ordinal);
            Assert.Contains(
                command.Parameters,
                parameter => parameter.Value is DateTime value
                    && value == new DateTime(2026, 8, 24, 12, 34, 56));
        }
    }

    private static object[] Case(
        SqlAgentToolType dialect,
        string scenario,
        string sql,
        string expectedRenderedFragments,
        string allowedTablesCsv) =>
        [dialect, scenario, sql, expectedRenderedFragments, allowedTablesCsv];

    private static bool SyntaxIntegerParameter(object? value, long expected) =>
        value switch
        {
            sbyte actual => actual == expected,
            byte actual => actual == expected,
            short actual => actual == expected,
            ushort actual => actual == expected,
            int actual => actual == expected,
            uint actual => actual == expected,
            long actual => actual == expected,
            ulong actual => actual <= long.MaxValue && (long)actual == expected,
            _ => false
        };
}
