using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class OracleNativeCapabilityGrammarMatrixTests
{
    private sealed record SourceShape(
        string Sql,
        string[] PhysicalTables);

    private sealed record OrderingShape(
        string Sql,
        NullOrderingKind Expected);

    private sealed record FetchShape(
        string Sql,
        int? RowCount,
        decimal? Percent,
        bool WithTies);

    private static readonly SqlProviderCapabilityProfile Profile =
        new(
            SqlAgentToolType.Oracle,
            ServerVersion: new Version(12, 1));

    private static readonly GrammarVariant<Func<string, string>>[] Placements =
    [
        new(
            "root",
            query => query),
        new(
            "cte-body",
            query => $"WITH native AS ({query}) SELECT id FROM native")
    ];

    private static readonly GrammarVariant<SourceShape>[] SourceShapes =
    [
        new(
            "simple-source",
            new SourceShape(
                " FROM users u",
                ["users"])),
        new(
            "join-source",
            new SourceShape(
                " FROM users u JOIN profiles p ON p.user_id = u.id",
                ["users", "profiles"])),
        new(
            "exists-source",
            new SourceShape(
                " FROM users u WHERE EXISTS (SELECT id FROM audit_log a WHERE a.user_id = u.id)",
                ["users", "audit_log"]))
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
            "rows-only",
            new FetchShape(
                " FETCH FIRST 5 ROWS ONLY",
                5,
                null,
                false)),
        new(
            "rows-with-ties",
            new FetchShape(
                " FETCH FIRST 5 ROWS WITH TIES",
                5,
                null,
                true)),
        new(
            "percent-only",
            new FetchShape(
                " FETCH FIRST 12.5 PERCENT ROWS ONLY",
                null,
                12.5m,
                false)),
        new(
            "percent-with-ties",
            new FetchShape(
                " FETCH FIRST 12.5 PERCENT ROWS WITH TIES",
                null,
                12.5m,
                true))
    ];

    public static IEnumerable<object[]> OracleNativeCapabilityMatrix()
    {
        foreach (var (placement, source, ordering, fetch) in
                 SyntaxGrammarMatrix.Product(
                     Placements,
                     SourceShapes,
                     OrderingShapes,
                     FetchShapes))
        {
            var nativeQuery =
                "SELECT u.id AS id" +
                source.Value.Sql +
                " ORDER BY u.id" +
                ordering.Value.Sql +
                fetch.Value.Sql;

            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "oracle-native",
                    placement.Name,
                    source.Name,
                    ordering.Name,
                    fetch.Name),
                placement.Name,
                placement.Value(nativeQuery),
                ordering.Value.Expected,
                fetch.Value.RowCount,
                fetch.Value.Percent,
                fetch.Value.WithTies,
                SyntaxGrammarMatrix.ExpectedTables(
                    source.Value.PhysicalTables)
            ];
        }
    }

    [Fact]
    public void OracleNativeCapabilityMatrix_IsCartesianAndCollisionFree()
    {
        var cases = OracleNativeCapabilityMatrix().ToArray();
        var expectedCount = SyntaxGrammarMatrix.ProductCount(
            Placements,
            SourceShapes,
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
    [MemberData(nameof(OracleNativeCapabilityMatrix))]
    public void OracleNativeCapabilityMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        string placement,
        string sql,
        NullOrderingKind expectedNullOrdering,
        int? expectedRowCount,
        decimal? expectedPercent,
        bool expectedWithTies,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Oracle,
            Profile);
        var native = NativeSelect(
            parsed.Statement,
            placement);

        var order = Assert.Single(native.OrderBy);
        Assert.Equal(expectedNullOrdering, order.NullOrdering);
        Assert.Equal(expectedWithTies, native.FetchWithTies);

        if (expectedRowCount.HasValue)
        {
            Assert.Equal(expectedRowCount.Value, native.Limit);
            Assert.False(native.FetchPercent.HasValue);
        }
        else
        {
            Assert.False(native.Limit.HasValue);
            Assert.True(native.FetchPercent.HasValue, name);
            Assert.Equal(expectedPercent!.Value, native.FetchPercent.Value);
        }

        var expectedTables = expectedTablesCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var facts = SqlCoreInspection.GetQueryFacts(parsed);
        Assert.Equal(expectedTables.Length, facts.ReferencedTables.Count);
        foreach (var table in expectedTables)
        {
            Assert.Contains(
                facts.ReferencedTables,
                actual => string.Equals(
                    actual,
                    table,
                    StringComparison.OrdinalIgnoreCase));
        }

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext(
                "oracle-native-capability-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy(),
            Profile,
            Profile);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FETCH NEXT", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (placement == "cte-body")
            Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (expectedNullOrdering == NullOrderingKind.First)
            Assert.Contains("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (expectedNullOrdering == NullOrderingKind.Last)
            Assert.Contains("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (expectedWithTies)
            Assert.Contains("WITH TIES", command.Sql, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("ROWS ONLY", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (expectedRowCount.HasValue)
        {
            Assert.True(
                command.Parameters.Any(parameter =>
                    SyntaxGrammarMatrix.IntegerParameterEquals(
                        parameter.Value,
                        expectedRowCount.Value)),
                name);
        }

        if (expectedPercent.HasValue)
        {
            Assert.Contains("PERCENT", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                command.Parameters,
                parameter => Equals(
                    parameter.Value,
                    expectedPercent.Value));
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
