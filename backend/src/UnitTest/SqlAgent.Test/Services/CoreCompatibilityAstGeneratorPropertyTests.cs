using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCompatibilityAstGeneratorPropertyTests
{
    private static readonly SourceSpan Span = SourceSpan.Unknown;

    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Firebird
    ];

    private sealed record AstCase(
        string Name,
        string EquivalentSql,
        Func<SqlAgentToolType, SelectStatement> Build,
        string[] ReferencedTables,
        bool ContainsSubquery = false);

    [Fact]
    public void GeneratedCompatibilityAsts_ConvergeWithTextPipelineAndTraverseDeterministically()
    {
        foreach (var provider in Providers)
        {
            foreach (var item in Cases())
            {
                var parsed = new ParsedStatement(item.Build(provider), provider);
                var first = CompileParsed(parsed, provider, item.Name);
                var second = CompileParsed(parsed, provider, item.Name);
                var fresh = CompileParsed(
                    new ParsedStatement(item.Build(provider), provider),
                    provider,
                    item.Name);
                var fromText = CompileText(item.EquivalentSql, provider, item.Name);

                AssertEquivalent(fromText, first, $"{provider}/{item.Name}/text-vs-ast");
                AssertEquivalent(first, second, $"{provider}/{item.Name}/same-ast-repeat");
                AssertEquivalent(first, fresh, $"{provider}/{item.Name}/fresh-ast");

                var facts = SqlCoreInspection.GetQueryFacts(parsed);
                Assert.Equal(item.ContainsSubquery, facts.ContainsSubquery);

                foreach (var table in item.ReferencedTables)
                {
                    Assert.Contains(
                        table,
                        facts.ReferencedTables,
                        StringComparer.OrdinalIgnoreCase);
                }

                Assert.Equal(
                    item.ReferencedTables.Length,
                    facts.ReferencedTables.Count);
            }
        }
    }

    private static IEnumerable<AstCase> Cases()
    {
        yield return new AstCase(
            "binary-predicate",
            "SELECT id, name FROM users WHERE id >= 1 ORDER BY id",
            provider => Select(
                [
                    Item(Column("id")),
                    Item(Column("name"))
                ],
                Binary(Column("id"), ">=", Literal(1)),
                [Order(Column("id"))]),
            ["users"]);

        yield return new AstCase(
            "arithmetic-projection",
            "SELECT id + 2 AS score FROM users WHERE id = 1",
            provider => Select(
                [Item(Binary(Column("id"), "+", Literal(2)), Alias("score", provider))],
                Binary(Column("id"), "=", Literal(1))),
            ["users"]);

        yield return new AstCase(
            "between-predicate",
            "SELECT id FROM users WHERE id BETWEEN 1 AND 3 ORDER BY id",
            provider => Select(
                [Item(Column("id"))],
                new BetweenExpr(
                    Column("id"),
                    Literal(1),
                    Literal(3),
                    false,
                    Span),
                [Order(Column("id"))]),
            ["users"]);

        yield return new AstCase(
            "in-list-predicate",
            "SELECT id FROM users WHERE id IN (1, 2, 3) ORDER BY id",
            provider => Select(
                [Item(Column("id"))],
                new InExpr(
                    Column("id"),
                    ImmutableArray.Create<SqlExpr>(
                        Literal(1),
                        Literal(2),
                        Literal(3)),
                    false,
                    Span),
                [Order(Column("id"))]),
            ["users"]);

        yield return new AstCase(
            "is-not-null-predicate",
            "SELECT name FROM users WHERE name IS NOT NULL ORDER BY id",
            provider => Select(
                [Item(Column("name"))],
                new IsNullExpr(Column("name"), true, Span),
                [Order(Column("id"))]),
            ["users"]);

        yield return new AstCase(
            "function-call",
            "SELECT LOWER(name) AS normalized_name FROM users WHERE id = 1",
            provider => Select(
                [
                    Item(
                        new FunctionCallExpr(
                            SqlIdentifier.Unquoted("LOWER"),
                            ImmutableArray.Create<SqlExpr>(Column("name")),
                            false,
                            Span),
                        Alias("normalized_name", provider))
                ],
                Binary(Column("id"), "=", Literal(1))),
            ["users"]);

        yield return new AstCase(
            "exists-subquery-traversal",
            "SELECT id FROM users WHERE EXISTS (SELECT id FROM archived_users WHERE id = 1)",
            () =>
            {
                var inner = Select(
                    [Item(Column("id"))],
                    Binary(Column("id"), "=", Literal(1)),
                    tableName: "archived_users");

                return Select(
                    [Item(Column("id"))],
                    new ExistsExpr(inner, false, Span));
            },
            ["users", "archived_users"],
            ContainsSubquery: true);
    }

    private static SelectStatement Select(
        IReadOnlyList<SelectItem> items,
        SqlExpr predicate,
        IReadOnlyList<OrderByItem>? orderBy = null,
        string tableName = "users") =>
        new(
            ImmutableArray<CteDefinition>.Empty,
            false,
            ImmutableArray.CreateRange(items),
            new NamedTableSource(
                SqlIdentifier.Unquoted(tableName),
                null!,
                Span),
            ImmutableArray<JoinSource>.Empty,
            predicate,
            ImmutableArray<SqlExpr>.Empty,
            null!,
            orderBy is null
                ? ImmutableArray<OrderByItem>.Empty
                : ImmutableArray.CreateRange(orderBy),
            new Nullable<int>(),
            new Nullable<int>(),
            Span);

    private static SelectItem Item(SqlExpr expression, string? alias = null) =>
        new(
            expression,
            alias is null
                ? null!
                : new IdentifierPart(alias, false, Span),
            Span);

    private static string Alias(string value, SqlAgentToolType provider) =>
        provider == SqlAgentToolType.Oracle
            ? value.ToUpperInvariant()
            : value;

    private static OrderByItem Order(SqlExpr expression) =>
        new(expression, false, NullOrderingKind.Default, Span);

    private static ColumnExpr Column(string name) =>
        new(SqlIdentifier.Unquoted(name), Span);

    private static LiteralExpr Literal(object value) =>
        new(value, Span);

    private static BinaryExpr Binary(SqlExpr left, string op, SqlExpr right) =>
        new(left, op, right, Span);

    private static CompiledSqlCommand CompileParsed(
        ParsedStatement parsed,
        SqlAgentToolType provider,
        string caseName) =>
        SqlCoreFacade.CompileQuery(
            parsed,
            provider,
            Validation(),
            new SqlExecutionPlanPolicy(100))
        ?? throw new InvalidOperationException(
            $"AST property compile returned null for {provider}/{caseName}.");

    private static CompiledSqlCommand CompileText(
        string sql,
        SqlAgentToolType provider,
        string caseName) =>
        SqlCoreFacade.CompileQuery(
            sql,
            provider,
            provider,
            Validation(),
            new SqlExecutionPlanPolicy(100))
        ?? throw new InvalidOperationException(
            $"Text property compile returned null for {provider}/{caseName}.");

    private static SqlPlanValidationContext Validation() =>
        new(
            "compatibility-ast-generator-v1",
            new HashSet<string>(
                new[] { "users", "archived_users" },
                StringComparer.OrdinalIgnoreCase));

    private static void AssertEquivalent(
        CompiledSqlCommand expected,
        CompiledSqlCommand actual,
        string label)
    {
        Assert.True(
            string.Equals(expected.Sql, actual.Sql, StringComparison.Ordinal),
            $"{label}: rendered SQL drifted. expected={expected.Sql} actual={actual.Sql}");
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.TargetProvider, actual.TargetProvider);
        Assert.Equal(expected.ReturnsRows, actual.ReturnsRows);
        Assert.Equal(expected.PlanFingerprint, actual.PlanFingerprint);
        Assert.Equal(expected.Parameters.Length, actual.Parameters.Length);

        for (var index = 0; index < expected.Parameters.Length; index++)
        {
            Assert.Equal(expected.Parameters[index].Name, actual.Parameters[index].Name);
            Assert.Equal(expected.Parameters[index].Value, actual.Parameters[index].Value);
        }

        var expectedEvidence = Assert.IsType<SqlCompileEvidence>(expected.CompileEvidence);
        var actualEvidence = Assert.IsType<SqlCompileEvidence>(actual.CompileEvidence);
        Assert.Equal(
            expectedEvidence.EvidenceFingerprint,
            actualEvidence.EvidenceFingerprint);
        Assert.Equal(
            SqlCompileDecisionBoundary.Completed,
            actualEvidence.DecisionBoundary);
        Assert.Equal(
            "SQL_COMPILE_TRANSLATED",
            actualEvidence.DecisionCode);
    }
}
