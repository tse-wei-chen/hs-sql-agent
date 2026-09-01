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

    private sealed record PolicyMutation(
        string Name,
        string BaselineSql,
        string Sql);

    private sealed record SyntaxMutation(
        string Name,
        string BaselineSql,
        string Sql);

    private sealed record CrossProviderMutation(
        string Name,
        SqlAgentToolType SourceDialect,
        SqlAgentToolType TargetDialect,
        string Sql,
        string[] MessageFragments);

    private static readonly GrammarVariant<SqlAgentToolType>[] AllDialects =
        Enum.GetValues<SqlAgentToolType>()
            .Select(dialect => new GrammarVariant<SqlAgentToolType>(
                dialect.ToString(),
                dialect))
            .ToArray();

    private static readonly GrammarVariant<PolicyMutation>[] PolicyMutations =
    [
        new(
            "update-without-where",
            new PolicyMutation(
                "update-without-where",
                "UPDATE users SET name = 'Alice' WHERE id = 1",
                "UPDATE users SET name = 'Alice'")),
        new(
            "delete-without-where",
            new PolicyMutation(
                "delete-without-where",
                "DELETE FROM users WHERE id = 1",
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

    private static readonly GrammarVariant<SqlAgentToolType>[] NoLimitDialects =
    [
        new("MsSqlServer", SqlAgentToolType.MsSqlServer),
        new("Oracle", SqlAgentToolType.Oracle),
        new("Firebird", SqlAgentToolType.Firebird)
    ];

    private static readonly GrammarVariant<SyntaxMutation>[] PostfixCastDmlContexts =
    [
        new(
            "update-set",
            new SyntaxMutation(
                "update-set",
                "UPDATE users SET name = CAST(id AS VARCHAR(20)) WHERE id = 1",
                "UPDATE users SET name = id::VARCHAR(20) WHERE id = 1")),
        new(
            "delete-predicate",
            new SyntaxMutation(
                "delete-predicate",
                "DELETE FROM users WHERE CAST(id AS VARCHAR(20)) = '1'",
                "DELETE FROM users WHERE id::VARCHAR(20) = '1'")),
        new(
            "insert-value",
            new SyntaxMutation(
                "insert-value",
                "INSERT INTO users (name) VALUES (CAST('1' AS VARCHAR(20)))",
                "INSERT INTO users (name) VALUES ('1'::VARCHAR(20))"))
    ];

    private static readonly GrammarVariant<SyntaxMutation>[] InsertSelectLimitContexts =
    [
        new(
            "insert-select",
            new SyntaxMutation(
                "insert-select",
                "INSERT INTO archive (id) SELECT id FROM users",
                "INSERT INTO archive (id) SELECT id FROM users LIMIT 5"))
    ];

    private static readonly GrammarVariant<CrossProviderMutation>[] CrossProviderDml =
    [
        new(
            "postgres-implicit-values-to-mysql",
            new CrossProviderMutation(
                "postgres-implicit-values-to-mysql",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL,
                "INSERT INTO users VALUES (1, 'Alice')",
                ["dml.insert_implicit_columns", "native-only"])),
        new(
            "postgres-implicit-select-to-mysql",
            new CrossProviderMutation(
                "postgres-implicit-select-to-mysql",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL,
                "INSERT INTO archive SELECT id, name FROM staged_users",
                ["dml.insert_implicit_columns", "native-only"])),
        new(
            "sqlserver-update-from-to-postgres",
            new CrossProviderMutation(
                "sqlserver-update-from-to-postgres",
                SqlAgentToolType.MsSqlServer,
                SqlAgentToolType.Postgres,
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id",
                ["dml.update.from", "native-only", "MsSqlServer", "Postgres"])),
        new(
            "sqlserver-update-from-extra-predicate-to-postgres",
            new CrossProviderMutation(
                "sqlserver-update-from-extra-predicate-to-postgres",
                SqlAgentToolType.MsSqlServer,
                SqlAgentToolType.Postgres,
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id AND users.active = 1",
                ["dml.update.from", "native-only", "MsSqlServer", "Postgres"])),
        new(
            "postgres-update-from-to-sqlserver",
            new CrossProviderMutation(
                "postgres-update-from-to-sqlserver",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MsSqlServer,
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id",
                ["dml.update.from", "native-only", "Postgres", "MsSqlServer"])),
        new(
            "postgres-update-from-extra-predicate-to-sqlserver",
            new CrossProviderMutation(
                "postgres-update-from-extra-predicate-to-sqlserver",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MsSqlServer,
                "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id AND users.active = TRUE",
                ["dml.update.from", "native-only", "Postgres", "MsSqlServer"]))
    ];

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
                mutation.Value.BaselineSql,
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

    public static IEnumerable<object[]> WrongDialectPostfixCastDmlMatrix()
    {
        foreach (var (dialect, mutation) in
                 SyntaxGrammarMatrix.Product(NonPostgresDialects, PostfixCastDmlContexts))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    "postgres-postfix-cast",
                    mutation.Name),
                dialect.Value,
                mutation.Value.BaselineSql,
                mutation.Value.Sql
            ];
        }
    }

    public static IEnumerable<object[]> WrongDialectInsertSelectLimitMatrix()
    {
        foreach (var (dialect, mutation) in
                 SyntaxGrammarMatrix.Product(NoLimitDialects, InsertSelectLimitContexts))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    dialect.Name,
                    "insert-select-limit"),
                dialect.Value,
                mutation.Value.BaselineSql,
                mutation.Value.Sql
            ];
        }
    }

    public static IEnumerable<object[]> CrossProviderDmlCapabilityMatrix()
    {
        foreach (var mutation in CrossProviderDml)
        {
            yield return
            [
                mutation.Name,
                mutation.Value.SourceDialect,
                mutation.Value.TargetDialect,
                mutation.Value.Sql,
                mutation.Value.MessageFragments
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
        var postfix = WrongDialectPostfixCastDmlMatrix().ToArray();
        var insertSelectLimit = WrongDialectInsertSelectLimitMatrix().ToArray();
        var crossProvider = CrossProviderDmlCapabilityMatrix().ToArray();

        Assert.Equal(12, policy.Length);
        Assert.Equal(18, malformed.Length);
        Assert.Equal(4, updateFrom.Length);
        Assert.Equal(5, deleteUsing.Length);
        Assert.Equal(5, firebirdUpsert.Length);
        Assert.Equal(15, postfix.Length);
        Assert.Equal(3, insertSelectLimit.Length);
        Assert.Equal(6, crossProvider.Length);
        Assert.Equal(
            68,
            policy
                .Concat(malformed)
                .Concat(updateFrom)
                .Concat(deleteUsing)
                .Concat(firebirdUpsert)
                .Concat(postfix)
                .Concat(insertSelectLimit)
                .Concat(crossProvider)
                .Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(PolicyMutationMatrix))]
    public void UnsafeDmlWithoutPredicate_BaselineSucceedsThenMutationFailsAtTypedPolicyStage(
        string name,
        SqlAgentToolType dialect,
        string baselineSql,
        string sql,
        string mutation)
    {
        var baseline = TryCompile(dialect, dialect, baselineSql);
        Assert.True(baseline.Success, name);

        var result = TryCompile(dialect, dialect, sql);

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
        var result = TryCompile(dialect, dialect, sql);

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
        var result = TryCompile(dialect, dialect, sql);

        Assert.False(result.Success, name);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SOURCE_CAPABILITY_REJECTED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Capability, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    [Theory]
    [MemberData(nameof(WrongDialectPostfixCastDmlMatrix))]
    public void PostgresPostfixCast_InNonPostgresDml_FailsAtSourceDialectBoundary(
        string name,
        SqlAgentToolType dialect,
        string baselineSql,
        string sql)
    {
        Assert.True(TryCompile(dialect, dialect, baselineSql).Success, name);

        var result = TryCompile(dialect, dialect, sql);

        Assert.False(result.Success, name);
        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SOURCE_DIALECT_SYNTAX", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.DialectSyntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.Equal(2, diagnostic.Span.Length);
    }

    [Theory]
    [MemberData(nameof(WrongDialectInsertSelectLimitMatrix))]
    public void InsertSelectLimit_InUnsupportedDmlDialects_FailsAtTypedParseStage(
        string name,
        SqlAgentToolType dialect,
        string baselineSql,
        string sql)
    {
        Assert.True(TryCompile(dialect, dialect, baselineSql).Success, name);
        AssertTypedParseGrammarFailure(name, dialect, sql);
    }

    [Theory]
    [MemberData(nameof(CrossProviderDmlCapabilityMatrix))]
    public void NativeDmlSucceedsButUnsupportedTargetFailsAtSemanticBoundary(
        string name,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect,
        string sql,
        string[] expectedMessageFragments)
    {
        Assert.True(
            TryCompile(sourceDialect, sourceDialect, sql).Success,
            name);

        var result = TryCompile(sourceDialect, targetDialect, sql);

        Assert.False(result.Success, name);
        foreach (var fragment in expectedMessageFragments)
        {
            Assert.Contains(
                fragment,
                result.ErrorMessage,
                StringComparison.OrdinalIgnoreCase);
        }

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_SEMANTIC_VALIDATION_FAILED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Semantic, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length >= 0, name);
    }

    private static void AssertTypedParseGrammarFailure(
        string name,
        SqlAgentToolType dialect,
        string sql)
    {
        var result = TryCompile(dialect, dialect, sql);

        Assert.False(result.Success, name);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);
        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_PARSE_GRAMMAR", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Parse, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0, name);
    }

    private static SqlCoreTryResult<CompiledSqlCommand> TryCompile(
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetDialect,
        string sql) =>
        SqlCoreFacade.TryCompileDml(
            sql,
            sourceDialect,
            targetDialect,
            new SqlPlanValidationContext(
                "negative-dml-grammar-mutation-matrix-v2"));
}
