using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeLexicalMutationMatrixTests
{
    private sealed record DialectLexicalVariant(
        SqlAgentToolType Dialect,
        string QuotedBaseline,
        string UnterminatedQuoted);

    private sealed record LexicalMutationShape(
        string Name,
        string BaselineSql,
        string MutatedSql,
        string MessageFragment,
        string SpanMarker,
        bool ExactSpan);

    private static readonly GrammarVariant<DialectLexicalVariant>[] Dialects =
    [
        new(
            "postgres",
            new(
                SqlAgentToolType.Postgres,
                "SELECT \"id\" FROM users",
                "SELECT \"id FROM users")),
        new(
            "mysql",
            new(
                SqlAgentToolType.MySQL,
                "SELECT `id` FROM users",
                "SELECT `id FROM users")),
        new(
            "sqlserver",
            new(
                SqlAgentToolType.MsSqlServer,
                "SELECT [id] FROM users",
                "SELECT [id FROM users")),
        new(
            "sqlite",
            new(
                SqlAgentToolType.Sqlite,
                "SELECT \"id\" FROM users",
                "SELECT \"id FROM users")),
        new(
            "oracle",
            new(
                SqlAgentToolType.Oracle,
                "SELECT \"id\" FROM users",
                "SELECT \"id FROM users")),
        new(
            "firebird",
            new(
                SqlAgentToolType.Firebird,
                "SELECT \"id\" FROM users",
                "SELECT \"id FROM users"))
    ];

    private static readonly GrammarVariant<LexicalMutationShape>[] SharedMutations =
    [
        new(
            "unterminated-string",
            new(
                "unterminated-string",
                "SELECT 'ok' FROM users",
                "SELECT 'unterminated FROM users",
                "Unterminated string literal",
                "'",
                false)),
        new(
            "unterminated-block-comment",
            new(
                "unterminated-block-comment",
                "SELECT id FROM users /* ok */",
                "SELECT id FROM users /* unterminated",
                "Unterminated block comment",
                "/*",
                false))
    ];

    private static readonly GrammarVariant<LexicalMutationShape>[] ParameterMutations =
    [
        new(
            "question",
            ParameterMutation("?", "?")),
        new(
            "colon",
            ParameterMutation(":name", ":name")),
        new(
            "at",
            ParameterMutation("@name", "@name")),
        new(
            "dollar",
            ParameterMutation("$1", "$1")),
        new(
            "template",
            ParameterMutation("{{name}}", "{{name}}"))
    ];

    public static IEnumerable<object[]> NegativeLexicalMutationMatrix()
    {
        foreach (var dialect in Dialects)
        foreach (var mutation in SharedMutations)
        {
            yield return Case(
                dialect,
                mutation);
        }

        foreach (var dialect in Dialects)
        foreach (var mutation in ParameterMutations)
        {
            yield return Case(
                dialect,
                mutation);
        }

        foreach (var dialect in Dialects)
        {
            var quoteMarker = dialect.Value.UnterminatedQuoted.Substring(
                "SELECT ".Length,
                1);
            var mutation = new GrammarVariant<LexicalMutationShape>(
                "unterminated-quoted-identifier",
                new LexicalMutationShape(
                    "unterminated-quoted-identifier",
                    dialect.Value.QuotedBaseline,
                    dialect.Value.UnterminatedQuoted,
                    "Unterminated quoted identifier",
                    quoteMarker,
                    false));

            yield return Case(
                dialect,
                mutation);
        }
    }

    [Fact]
    public void NegativeLexicalMutationMatrix_IsCombinatorialAndCollisionFree()
    {
        var cases = NegativeLexicalMutationMatrix().ToArray();
        var expectedCount =
            Dialects.Length * SharedMutations.Length +
            Dialects.Length * ParameterMutations.Length +
            Dialects.Length;

        Assert.Equal(48, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(NegativeLexicalMutationMatrix))]
    public void NegativeLexicalMutationMatrix_BaselineParsesButMutationFailsInLexer(
        string name,
        SqlAgentToolType dialect,
        string baselineSql,
        string mutatedSql,
        string expectedMessageFragment,
        string expectedSpanMarker,
        bool exactSpan)
    {
        var baseline = CoreSqlTextParser.ParseQuery(
            baselineSql,
            dialect);

        Assert.NotNull(baseline.Statement);

        var error = Record.Exception(
            () => CoreSqlTextParser.ParseQuery(
                mutatedSql,
                dialect));

        Assert.NotNull(error);
        Assert.Equal(typeof(SqlParseException), error.GetType());
        Assert.Contains(
            expectedMessageFragment,
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);

        Assert.Equal("SQL_LEXICAL_ERROR", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length > 0, name);
        Assert.True(diagnostic.Span.End <= mutatedSql.Length, name);

        var actualSpanText = mutatedSql.Substring(
            diagnostic.Span.Start,
            diagnostic.Span.Length);

        if (exactSpan)
        {
            Assert.Equal(
                expectedSpanMarker,
                actualSpanText,
                ignoreCase: true);
        }
        else
        {
            Assert.StartsWith(
                expectedSpanMarker,
                actualSpanText,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static LexicalMutationShape ParameterMutation(
        string parameter,
        string spanText) =>
        new(
            parameter,
            "SELECT 1 FROM users",
            $"SELECT {parameter} FROM users",
            "Unbound SQL parameter",
            spanText,
            true);

    private static object[] Case(
        GrammarVariant<DialectLexicalVariant> dialect,
        GrammarVariant<LexicalMutationShape> mutation) =>
        [
            SyntaxGrammarMatrix.CaseName(
                "lexical",
                dialect.Name,
                mutation.Name),
            dialect.Value.Dialect,
            mutation.Value.BaselineSql,
            mutation.Value.MutatedSql,
            mutation.Value.MessageFragment,
            mutation.Value.SpanMarker,
            mutation.Value.ExactSpan
        ];
}
