using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeRecursiveCteGrammarMutationMatrixTests
{
    private sealed record ProviderVariant(
        SqlAgentToolType Dialect,
        SqlProviderCapabilityProfile Profile,
        string AnchorSql);

    private sealed record MutationVariant(
        Func<ProviderVariant, string> Sql,
        string MessageFragment);

    private static readonly GrammarVariant<ProviderVariant>[] Providers =
    [
        new(
            "postgres",
            new ProviderVariant(
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 16, 0),
                "SELECT 1")),
        new(
            "mysql",
            new ProviderVariant(
                SqlAgentToolType.MySQL,
                Profile(SqlAgentToolType.MySQL, 8, 0, 1),
                "SELECT 1")),
        new(
            "sqlite",
            new ProviderVariant(
                SqlAgentToolType.Sqlite,
                Profile(SqlAgentToolType.Sqlite, 3, 8, 3),
                "SELECT 1")),
        new(
            "firebird",
            new ProviderVariant(
                SqlAgentToolType.Firebird,
                Profile(SqlAgentToolType.Firebird, 2, 1),
                "SELECT 1 FROM RDB$DATABASE"))
    ];

    private static readonly GrammarVariant<ProviderVariant>[] NonPostgresProviders =
        Providers
            .Where(provider =>
                provider.Value.Dialect != SqlAgentToolType.Postgres)
            .ToArray();

    private static readonly GrammarVariant<MutationVariant>[] CommonShapeMutations =
    [
        new(
            "anchor-self-reference",
            new MutationVariant(
                _ =>
                    "WITH RECURSIVE x(n) AS (" +
                    "SELECT n FROM x UNION ALL SELECT n + 1 FROM x WHERE n < 3" +
                    ") SELECT n FROM x",
                "anchor")),
        new(
            "duplicate-direct-self-reference",
            new MutationVariant(
                provider =>
                    $"WITH RECURSIVE x(n) AS ({provider.AnchorSql} " +
                    "UNION ALL SELECT a.n + b.n FROM x a JOIN x b ON a.n = b.n" +
                    ") SELECT n FROM x",
                "exactly one direct self-reference")),
        new(
            "nested-only-self-reference",
            new MutationVariant(
                provider =>
                    $"WITH RECURSIVE x(n) AS ({provider.AnchorSql} " +
                    "UNION ALL SELECT 1 + (SELECT n FROM x) FROM step_guard" +
                    ") SELECT n FROM x",
                "exactly one direct self-reference")),
        new(
            "multiple-recursive-set-branches",
            new MutationVariant(
                provider =>
                    $"WITH RECURSIVE x(n) AS ({provider.AnchorSql} " +
                    "UNION ALL SELECT n + 1 FROM x WHERE n < 2 " +
                    "UNION ALL SELECT n + 2 FROM x WHERE n < 3" +
                    ") SELECT n FROM x",
                "one anchor UNION"))
    ];

    private static readonly GrammarVariant<MutationVariant>[] PortableSubsetMutations =
    [
        new(
            "aggregate-recursive-member",
            new MutationVariant(
                provider =>
                    $"WITH RECURSIVE x(n) AS ({provider.AnchorSql} " +
                    "UNION ALL SELECT SUM(n) FROM x" +
                    ") SELECT n FROM x",
                "portable recursive-member subset")),
        new(
            "distinct-recursive-member",
            new MutationVariant(
                provider =>
                    $"WITH RECURSIVE x(n) AS ({provider.AnchorSql} " +
                    "UNION ALL SELECT DISTINCT n + 1 FROM x WHERE n < 3" +
                    ") SELECT n FROM x",
                "portable recursive-member subset"))
    ];

    public static IEnumerable<object[]> CommonRecursiveShapeMutationMatrix()
    {
        foreach (var (provider, mutation) in
                 SyntaxGrammarMatrix.Product(Providers, CommonShapeMutations))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "recursive-negative",
                    provider.Name,
                    mutation.Name),
                provider.Value,
                Baseline(provider.Value),
                mutation.Value.Sql(provider.Value),
                mutation.Value.MessageFragment
            ];
        }
    }

    public static IEnumerable<object[]> PortableSubsetMutationMatrix()
    {
        foreach (var (provider, mutation) in
                 SyntaxGrammarMatrix.Product(
                     NonPostgresProviders,
                     PortableSubsetMutations))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "recursive-negative",
                    provider.Name,
                    mutation.Name),
                provider.Value,
                Baseline(provider.Value),
                mutation.Value.Sql(provider.Value),
                mutation.Value.MessageFragment
            ];
        }
    }

    public static IEnumerable<object[]> FirebirdUnionMutationMatrix()
    {
        var provider = Providers.Single(
            item => item.Value.Dialect == SqlAgentToolType.Firebird);

        yield return
        [
            SyntaxGrammarMatrix.CaseName(
                "recursive-negative",
                provider.Name,
                "union-distinct-recursive-member"),
            provider.Value,
            Baseline(provider.Value),
            "WITH RECURSIVE x(n) AS (" +
            "SELECT 1 FROM RDB$DATABASE " +
            "UNION SELECT n + 1 FROM x WHERE n < 3" +
            ") SELECT n FROM x",
            "UNION ALL"
        ];
    }

    [Fact]
    public void RecursiveNegativeMatrices_HaveStableCoverage()
    {
        var common = CommonRecursiveShapeMutationMatrix().ToArray();
        var portableSubset = PortableSubsetMutationMatrix().ToArray();
        var firebird = FirebirdUnionMutationMatrix().ToArray();

        Assert.Equal(16, common.Length);
        Assert.Equal(6, portableSubset.Length);
        Assert.Single(firebird);
        Assert.Equal(
            23,
            common
                .Concat(portableSubset)
                .Concat(firebird)
                .Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(CommonRecursiveShapeMutationMatrix))]
    [MemberData(nameof(PortableSubsetMutationMatrix))]
    [MemberData(nameof(FirebirdUnionMutationMatrix))]
    public void RecursiveGrammarMutations_BaselineCompilesButMutationFailsAtTypedBinding(
        string name,
        ProviderVariant provider,
        string baselineSql,
        string mutatedSql,
        string messageFragment)
    {
        var baseline = Compile(
            provider,
            baselineSql);

        Assert.False(string.IsNullOrWhiteSpace(baseline.Sql), name);

        var error = Record.Exception(
            () => Compile(
                provider,
                mutatedSql));

        Assert.NotNull(error);
        Assert.IsType<SqlCompilationException>(error);
        Assert.Contains(
            messageFragment,
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);
        Assert.Equal("SQL_BINDING_ERROR", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Binding, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length > 0, name);
        Assert.True(diagnostic.Span.End <= mutatedSql.Length, name);
    }

    private static string Baseline(ProviderVariant provider) =>
        $"WITH RECURSIVE x(n) AS ({provider.AnchorSql} " +
        "UNION ALL SELECT n + 1 FROM x WHERE n < 3" +
        ") SELECT n FROM x";

    private static CompiledSqlCommand Compile(
        ProviderVariant provider,
        string sql)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            provider.Dialect,
            provider.Profile);

        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            provider.Dialect,
            new SqlPlanValidationContext(
                "negative-recursive-cte-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy(),
            provider.Profile);
    }

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
