using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresNativeCapabilityGrammarMatrixTests
{
    private sealed record SelectMode(
        string Sql,
        bool DistinctOn);

    private sealed record LateralShape(
        string Sql,
        string? RenderedMarker);

    private sealed record OrderingShape(
        string Sql,
        NullOrderingKind Expected);

    private sealed record FetchShape(
        string Sql,
        bool WithTies);

    private static readonly SqlProviderCapabilityProfile Profile =
        new(
            SqlAgentToolType.Postgres,
            ServerVersion: new Version(13, 0));

    private static readonly GrammarVariant<Func<string, string>>[] Placements =
    [
        new(
            "root",
            query => query),
        new(
            "cte-body",
            query => $"WITH native AS ({query}) SELECT id FROM native")
    ];

    private static readonly GrammarVariant<SelectMode>[] SelectModes =
    [
        new(
            "plain",
            new SelectMode(
                "SELECT u.id AS id",
                false)),
        new(
            "distinct-on",
            new SelectMode(
                "SELECT DISTINCT ON (u.id) u.id AS id",
                true))
    ];

    private static readonly GrammarVariant<LateralShape>[] LateralShapes =
    [
        new(
            "no-lateral",
            new LateralShape(
                " FROM users u",
                null)),
        new(
            "cross-lateral",
            new LateralShape(
                " FROM users u CROSS JOIN LATERAL (SELECT u.id AS id) q",
                "CROSS JOIN LATERAL")),
        new(
            "left-lateral",
            new LateralShape(
                " FROM users u LEFT JOIN LATERAL (SELECT u.id AS id) q ON TRUE",
                "LEFT JOIN LATERAL"))
    ];

    private static readonly GrammarVariant<OrderingShape>[] OrderingShapes =
    [
        new(
            "default-null-order",
            new OrderingShape(
                "",
                NullOrderingKind.Default)),
        new(
            "nulls-first",
            new OrderingShape(
                " NULLS FIRST",
                NullOrderingKind.First)),
        new(
            "nulls-last",
            new OrderingShape(
                " NULLS LAST",
                NullOrderingKind.Last))
    ];

    private static readonly GrammarVariant<FetchShape>[] FetchShapes =
    [
        new(
            "no-fetch",
            new FetchShape(
                "",
                false)),
        new(
            "fetch-with-ties",
            new FetchShape(
                " FETCH FIRST 5 ROWS WITH TIES",
                true))
    ];

    public static IEnumerable<object[]> PostgresNativeCapabilityMatrix()
    {
        foreach (var (placement, select, lateral, ordering, fetch) in
                 SyntaxGrammarMatrix.Product(
                     Placements,
                     SelectModes,
                     LateralShapes,
                     OrderingShapes,
                     FetchShapes))
        {
            var nativeQuery =
                select.Value.Sql +
                lateral.Value.Sql +
                " ORDER BY u.id" +
                ordering.Value.Sql +
                fetch.Value.Sql;

            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "postgres-native",
                    placement.Name,
                    select.Name,
                    lateral.Name,
                    ordering.Name,
                    fetch.Name),
                placement.Name,
                placement.Value(nativeQuery),
                select.Value.DistinctOn,
                lateral.Value.RenderedMarker,
                ordering.Value.Expected,
                fetch.Value.WithTies
            ];
        }
    }

    [Fact]
    public void PostgresNativeCapabilityMatrix_IsCartesianAndCollisionFree()
    {
        var cases = PostgresNativeCapabilityMatrix().ToArray();
        var expectedCount = SyntaxGrammarMatrix.ProductCount(
            Placements,
            SelectModes,
            LateralShapes,
            OrderingShapes,
            FetchShapes);

        Assert.Equal(72, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[2]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(PostgresNativeCapabilityMatrix))]
    public void PostgresNativeCapabilityMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string placement,
        string sql,
        bool expectedDistinctOn,
        string? lateralRenderedMarker,
        NullOrderingKind expectedNullOrdering,
        bool expectedFetchWithTies)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres,
            Profile);
        var native = NativeSelect(
            parsed.Statement,
            placement);

        Assert.Equal(expectedDistinctOn, !native.DistinctOn.IsDefaultOrEmpty);
        Assert.Equal(
            expectedDistinctOn ? 1 : 0,
            native.DistinctOn.Length);

        var order = Assert.Single(native.OrderBy);
        Assert.Equal(expectedNullOrdering, order.NullOrdering);
        Assert.Equal(expectedFetchWithTies, native.FetchWithTies);

        if (expectedFetchWithTies)
            Assert.Equal(5, native.Limit);
        else
            Assert.False(native.Limit.HasValue);

        if (lateralRenderedMarker is null)
        {
            Assert.Empty(native.Joins);
        }
        else
        {
            var join = Assert.Single(native.Joins);
            var derived = Assert.IsType<DerivedTableSource>(join.Source);
            Assert.True(derived.IsLateral, name);
        }

        var facts = SqlCoreInspection.GetQueryFacts(parsed);
        Assert.Single(facts.ReferencedTables);
        Assert.Contains(
            facts.ReferencedTables,
            table => string.Equals(
                table,
                "users",
                StringComparison.OrdinalIgnoreCase));

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "postgres-native-capability-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy(),
            Profile,
            Profile);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (placement == "cte-body")
            Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (expectedDistinctOn)
            Assert.Contains("DISTINCT ON", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (lateralRenderedMarker is not null)
            Assert.Contains(
                lateralRenderedMarker,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);

        if (expectedNullOrdering == NullOrderingKind.First)
            Assert.Contains("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (expectedNullOrdering == NullOrderingKind.Last)
            Assert.Contains("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (expectedFetchWithTies)
        {
            Assert.Contains("WITH TIES", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                command.Parameters.Any(parameter =>
                    SyntaxGrammarMatrix.IntegerParameterEquals(
                        parameter.Value,
                        5L)),
                name);
        }
    }

    private static SelectStatement NativeSelect(
        SqlStatement statement,
        string placement)
    {
        var root = Assert.IsType<SelectStatement>(statement);
        if (placement == "root")
            return root;

        var cte = Assert.Single(root.Ctes);
        return Assert.IsType<SelectStatement>(cte.Query);
    }
}
