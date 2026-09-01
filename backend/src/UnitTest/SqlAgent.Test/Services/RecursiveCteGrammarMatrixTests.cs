using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class RecursiveCteGrammarMatrixTests
{
    private sealed record ProviderVariant(
        SqlAgentToolType Dialect,
        SqlProviderCapabilityProfile Profile,
        string AnchorSql,
        string[] AnchorTables);

    private sealed record RecursiveMemberVariant(
        string Sql,
        string[] PhysicalTables);

    private sealed record RootVariant(
        string Sql,
        string[] PhysicalTables);

    private sealed record TailVariant(string Sql);

    private static readonly GrammarVariant<ProviderVariant>[] Providers =
    [
        new(
            "postgres",
            new ProviderVariant(
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 16, 0),
                "SELECT 1",
                [])),
        new(
            "mysql",
            new ProviderVariant(
                SqlAgentToolType.MySQL,
                Profile(SqlAgentToolType.MySQL, 8, 0, 1),
                "SELECT 1",
                [])),
        new(
            "sqlite",
            new ProviderVariant(
                SqlAgentToolType.Sqlite,
                Profile(SqlAgentToolType.Sqlite, 3, 8, 3),
                "SELECT 1",
                [])),
        new(
            "firebird",
            new ProviderVariant(
                SqlAgentToolType.Firebird,
                Profile(SqlAgentToolType.Firebird, 2, 1),
                "SELECT 1 FROM RDB$DATABASE",
                ["RDB$DATABASE"]))
    ];

    private static readonly GrammarVariant<RecursiveMemberVariant>[] Members =
    [
        new(
            "increment",
            new RecursiveMemberVariant(
                "SELECT n + 1 FROM x WHERE n < 3",
                [])),
        new(
            "arithmetic-expression",
            new RecursiveMemberVariant(
                "SELECT n + 2 - 1 FROM x WHERE n < 3",
                [])),
        new(
            "inner-join-physical",
            new RecursiveMemberVariant(
                "SELECT x.n + 1 FROM x JOIN step_guard g ON g.id = x.n WHERE x.n < 3",
                ["step_guard"])),
        new(
            "cross-join-physical",
            new RecursiveMemberVariant(
                "SELECT x.n + 1 FROM x CROSS JOIN step_guard g WHERE g.id = x.n AND x.n < 3",
                ["step_guard"]))
    ];

    private static readonly GrammarVariant<RootVariant>[] Roots =
    [
        new(
            "select",
            new RootVariant(
                "SELECT n FROM x",
                [])),
        new(
            "physical-join",
            new RootVariant(
                "SELECT x.n FROM x JOIN result_guard g ON g.id = x.n",
                ["result_guard"])),
        new(
            "correlated-exists",
            new RootVariant(
                "SELECT n FROM x WHERE EXISTS (SELECT id FROM result_guard g WHERE g.id = x.n)",
                ["result_guard"])),
        new(
            "root-set-operation",
            new RootVariant(
                "SELECT n FROM x UNION ALL SELECT id FROM result_guard",
                ["result_guard"]))
    ];

    private static readonly GrammarVariant<TailVariant>[] Tails =
    [
        new("none", new TailVariant("")),
        new("order", new TailVariant(" ORDER BY n"))
    ];

    public static IEnumerable<object[]> RecursiveCteGrammarMatrix()
    {
        foreach (var (provider, member, root, tail) in
                 SyntaxGrammarMatrix.Product(Providers, Members, Roots, Tails))
        {
            var sql =
                $"WITH RECURSIVE x(n) AS ({provider.Value.AnchorSql} UNION ALL {member.Value.Sql}) " +
                root.Value.Sql +
                tail.Value.Sql;
            var expectedTablesCsv = SyntaxGrammarMatrix.ExpectedTables(
                provider.Value.AnchorTables,
                member.Value.PhysicalTables,
                root.Value.PhysicalTables);

            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "recursive",
                    provider.Name,
                    member.Name,
                    root.Name,
                    tail.Name),
                provider.Value.Dialect,
                provider.Value.Profile,
                sql,
                expectedTablesCsv
            ];
        }
    }

    [Fact]
    public void RecursiveCteGrammarMatrix_IsCartesianAndCollisionFree()
    {
        var cases = RecursiveCteGrammarMatrix().ToArray();
        var expectedCount = SyntaxGrammarMatrix.ProductCount(
            Providers,
            Members,
            Roots,
            Tails);

        Assert.Equal(128, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[3]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(RecursiveCteGrammarMatrix))]
    public void RecursiveCteGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        SqlAgentToolType dialect,
        SqlProviderCapabilityProfile profile,
        string sql,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            dialect,
            profile);

        var head = Head(parsed.Statement);
        var cte = Assert.Single(head.Ctes);
        Assert.True(cte.RecursiveScope, name);
        var columnAlias = Assert.Single(cte.ColumnAliases);
        Assert.Equal(
            "n",
            Assert.Single(columnAlias.Parts).Value,
            ignoreCase: true);

        var cteQuery = Assert.IsType<QueryStatement>(cte.Query);
        var recursiveBranch = Assert.Single(cteQuery.SetOperations);
        Assert.Equal(SetOperationKind.UnionAll, recursiveBranch.Kind);

        var facts = SqlCoreInspection.GetQueryFacts(parsed);
        var expectedTables = expectedTablesCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(facts.ContainsCte, name);
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
            dialect,
            dialect,
            new SqlPlanValidationContext(
                "recursive-cte-combinatorial-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy(),
            profile,
            profile);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.Contains(
            "WITH RECURSIVE",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "UNION ALL",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);

        foreach (var table in expectedTables)
        {
            Assert.Contains(
                table,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }

        if (name.Contains("__inner-join-physical__", StringComparison.Ordinal))
            Assert.Contains("JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (name.Contains("__cross-join-physical__", StringComparison.Ordinal))
            Assert.Contains("CROSS JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (name.Contains("__correlated-exists__", StringComparison.Ordinal))
            Assert.Contains("EXISTS", command.Sql, StringComparison.OrdinalIgnoreCase);

        if (name.Contains("__root-set-operation__", StringComparison.Ordinal))
            Assert.True(
                command.Sql.Split(
                    "UNION ALL",
                    StringSplitOptions.None).Length >= 3,
                name);

        if (name.EndsWith("__order", StringComparison.Ordinal))
            Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static SelectStatement Head(SqlStatement statement) =>
        statement switch
        {
            SelectStatement select => select,
            QueryStatement query => query.Head,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected recursive query AST, got {statement.GetType().Name}.")
        };

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor,
        int? build = null) =>
        new(
            provider,
            ServerVersion: build.HasValue
                ? new Version(major, minor, build.Value)
                : new Version(major, minor));
}
