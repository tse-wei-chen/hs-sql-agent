using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreHistoricalSemanticDriftMutationTests
{
    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Firebird
    ];

    [Fact]
    public void PostgresDistinctFrom_IsNotCollapsedIntoOrdinaryInequality()
    {
        var distinct = CompileQuery(
            "SELECT id FROM users WHERE name IS DISTINCT FROM 'Ada'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        var ordinary = CompileQuery(
            "SELECT id FROM users WHERE name <> 'Ada'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains(
            "IS DISTINCT FROM",
            distinct.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(distinct.PlanFingerprint, ordinary.PlanFingerprint);
        Assert.NotEqual(distinct.Sql, ordinary.Sql);
    }

    [Fact]
    public void PostgresIlike_ToMySql_RemainsTargetCapabilityRejectionRatherThanLikeDowngrade()
    {
        var result = SqlCoreFacade.TryCompileQuery(
            "SELECT id FROM users WHERE name ILIKE 'a%'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            Validation(),
            new SqlExecutionPlanPolicy(100));

        Assert.False(result.Success);
        var evidence = Assert.IsType<SqlCompileEvidence>(result.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.Equal(
            SqlCompileDecisionBoundary.TargetCapability,
            evidence.DecisionBoundary);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public void MergeAndMatched_RemainContextualIdentifiersOutsideMergeGrammar(
        SqlAgentToolType provider)
    {
        var command = CompileQuery(
            "SELECT merge AS matched FROM users",
            provider,
            provider);

        Assert.Equal(SqlStatementKind.Query, command.Kind);
        Assert.Contains(
            "merge",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortableCte_RemainsAcceptedAcrossAllSixProviders()
    {
        foreach (var provider in Providers)
        {
            var command = CompileQuery(
                "WITH x AS (SELECT id FROM users WHERE id > 0) " +
                "SELECT id FROM x ORDER BY id",
                provider,
                provider);

            Assert.Equal(SqlStatementKind.Query, command.Kind);
            Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RichConflictUpdate_OperatorPrecedenceMutationChangesExecutableIdentity()
    {
        const string leftAssociative =
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) " +
            "ON CONFLICT (id) DO UPDATE SET quantity = quantity + excluded.quantity * 2";

        const string parenthesized =
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) " +
            "ON CONFLICT (id) DO UPDATE SET quantity = (quantity + excluded.quantity) * 2";

        var first = CompilePostgresUpsert(leftAssociative);
        var second = CompilePostgresUpsert(parenthesized);

        Assert.NotEqual(first.PlanFingerprint, second.PlanFingerprint);
        Assert.NotEqual(first.Sql, second.Sql);
        Assert.Contains("*", first.Sql, StringComparison.Ordinal);
        Assert.Contains("*", second.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CastTypeModifier_RemainsPartOfRenderedSemanticType()
    {
        var command = CompileQuery(
            "SELECT CAST(name AS VARCHAR(20)) FROM users WHERE id = 1",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains(
            "VARCHAR(20)",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompilePostgresUpsert(string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            sql,
            SqlAgentToolType.Postgres);

        return CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            Validation(),
            conflictTargetAssurance:
                DmlConflictTargetAssurance.FromPrimaryKey(["id"]))
            ?? throw new InvalidOperationException(
                "Historical conflict-update mutation compile returned null.");
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(
            sql,
            source,
            target,
            Validation(),
            new SqlExecutionPlanPolicy(100))
        ?? throw new InvalidOperationException(
            $"Historical semantic-drift compile returned null for {source}->{target}: {sql}");

    private static SqlPlanValidationContext Validation() =>
        new(
            "historical-semantic-drift-v1",
            new HashSet<string>(
                new[] { "users", "inventory" },
                StringComparer.OrdinalIgnoreCase));
}
