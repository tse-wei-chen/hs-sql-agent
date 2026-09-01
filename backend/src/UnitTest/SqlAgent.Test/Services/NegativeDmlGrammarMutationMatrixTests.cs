using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeDmlGrammarMutationMatrixTests
{
    private sealed record Mutation(
        string Name,
        string Sql);

    private static readonly GrammarVariant<SqlAgentToolType>[] AllDialects =
        Enum.GetValues<SqlAgentToolType>()
            .Select(dialect => new GrammarVariant<SqlAgentToolType>(
                dialect.ToString(),
                dialect))
            .ToArray();

    private static readonly GrammarVariant<Mutation>[] PolicyMutations =
    [
        new(
            "update-without-where",
            new Mutation(
                "update-without-where",
                "UPDATE users SET name = 'Alice'")),
        new(
            "delete-without-where",
            new Mutation(
                "delete-without-where",
                "DELETE FROM users"))
    ];

    private static readonly GrammarVariant<Mutation>[] MalformedDml =
    [
        new(
            "implicit-insert-row-width",
            new Mutation(
                "implicit-insert-row-width",
                "INSERT INTO users VALUES (1, 'Alice'), (2)")),
        new(
            "duplicate-update-assignment",
            new Mutation(
                "duplicate-update-assignment",
                "UPDATE users SET name = 'Alice', name = 'Bob' WHERE id = 1")),
        new(
            "multiple-statements",
            new Mutation(
                "multiple-statements",
                "DELETE FROM users WHERE id = 1; DELETE FROM audit WHERE id = 1"))
    ];

    private static readonly GrammarVariant<SqlAgentToolType>[] NoUpdateFromDialects =
        AllDialects
            .Where(item =>
                item.Value is not SqlAgentToolType.Postgres
                    and not SqlAgentToolType.MsSqlServer)
            .ToArray();

    private static readonly GrammarVariant<SqlAgentToolType>[] NonPostgresDialects =
        AllDialects
            .Where(item => item.Value != SqlAgentToolType.Postgres)
            .ToArray();

    private static readonly GrammarVariant<SqlAgentToolType>[] NonFirebirdDialects =
        AllDialects
            .Where(item => item.Value != SqlAgentToolType.Firebird)
            .ToArray();

    public static IEnumerable<object[]> PolicyMutationMatrix()
    {
        foreach (var (dialect, mutation) in
                 SyntaxGrammarMatrix.Product(AllDialects, PolicyMutations))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    mutation.Name),
                dialect.Value,
                mutation.Value.Sql,
                mutation.Name
            ];
        }
    }

    public static IEnumerable<object[]> MalformedDmlMatrix()
    {
        foreach (var (dialect, mutation) in
                 SyntaxGrammarMatrix.Product(AllDialects, MalformedDml))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    mutation.Name),
                dialect.Value,
                mutation.Value.Sql
            ];
        }
    }

    public static IEnumerable<object[]> UpdateFromWrongSourceMatrix()
    {
        foreach (var dialect in NoUpdateFromDialects)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    "update-from-wrong-source"),
                dialect.Value,
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id"
            ];
        }
    }

    public static IEnumerable<object[]> DeleteUsingWrongSourceMatrix()
    {
        foreach (var dialect in NonPostgresDialects)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    "delete-using-wrong-source"),
                dialect.Value,
                "DELETE FROM users USING profiles WHERE users.id = profiles.user_id"
            ];
        }
    }

    public static IEnumerable<object[]> FirebirdUpsertWrongSourceMatrix()
    {
        foreach (var dialect in NonFirebirdDialects)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    "firebird-update-or-insert-wrong-source"),
                dialect.Value,
                "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'Alice') MATCHING (id)"
            ];
        }
    }

    [Fact]
    public void NegativeDmlMatrices_HaveStableCoverage()
    {
        var policy = PolicyMutationMatrix().ToArray();
        var malformed = MalformedDmlMatrix().ToArray();
        var updateFrom = UpdateFromWrongSourceMatrix().ToArray();
        var deleteUsing = DeleteUsingWrongSourceMatrix().ToArray();
        var firebirdUpsert = FirebirdUpsertWrongSourceMatrix().ToArray();

        Assert.Equal(12, policy.Length);
        Assert.Equal(18, malformed.Length);
        Assert.Equal(4, updateFrom.Length);
        Assert.Equal(5, deleteUsing.Length);
        Assert.Equal(5, firebirdUpsert.Length);
        Assert.Equal(
            44,
            policy
                .Concat(malformed)
                .Concat(updateFrom)
                .Concat(deleteUsing)
                .Concat(firebirdUpsert)
                .Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(PolicyMutationMatrix))]
    public void UnsafeDmlWithoutPredicate_FailsAtTypedPolicyStage(
        string name,
        SqlAgentToolType dialect,
        string sql,
        string mutation)
    {
        var result = TryCompile(dialect, sql);

        Assert.False(result.Success, name);
        Assert.Equal("SQL_POLICY_DENIED", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Policy, diagnostic.Category);
        Assert.Equal(
            mutation == "update-without-where"
                ? "SQL_POLICY_UPDATE_REQUIRES_WHERE"
                : "SQL_POLICY_DELETE_REQUIRES_WHERE",
            diagnostic.Code);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(MalformedDmlMatrix))]
    [MemberData(nameof(DeleteUsingWrongSourceMatrix))]
    [MemberData(nameof(FirebirdUpsertWrongSourceMatrix))]
    public void MalformedOrWrongDialectDml_FailsClosedAtTypedParseStage(
        string name,
        SqlAgentToolType dialect,
        string sql)
    {
        var result = TryCompile(dialect, sql);

        Assert.False(result.Success, name);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_PARSE_GRAMMAR", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Parse, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(UpdateFromWrongSourceMatrix))]
    public void UpdateFrom_InUnsupportedSourceDialects_FailsAtSourceCapabilityStage(
        string name,
        SqlAgentToolType dialect,
        string sql)
    {
        var result = TryCompile(dialect, sql);

        Assert.False(result.Success, name);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    private static SqlCoreTryResult<CompiledSqlCommand> TryCompile(
        SqlAgentToolType dialect,
        string sql) =>
        SqlCoreFacade.TryCompileDml(
            sql,
            dialect,
            dialect,
            new SqlPlanValidationContext(
                "negative-dml-grammar-mutation-matrix-v1"));
}
