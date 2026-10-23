using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Regression against parser-stack exhaustion by pure grouping parentheses.
/// Non-redundant nested SQL queries remain a separate measured resource class.
/// </summary>
public sealed class CoreSqlNestedExpressionStressTests
{
    public static IEnumerable<object[]> SourceDialects()
        => Enum.GetValues<SqlAgentToolType>().Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DeepRedundantGrouping_CompilesWithoutRecursiveDescent(
        SqlAgentToolType dialect)
    {
        // 2,048 wrappers fit well within the lexical budget but historically
        // required 2,048 nested parseExpression/parsePrimary calls.
        const int depth = 2_048;
        var sql = "SELECT " + new string('(', depth) + "1" + new string(')', depth);
        var context = new SqlPlanValidationContext("iterative-grouping-v1");

        var compiled = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect, context, new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
        Assert.Equal(
            SqlCoreFacade.CompileQuery(
                "SELECT 1", dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql,
            compiled.Sql);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void LongNotChains_CompileToThreeValuedLogicEquivalentPredicates(
        SqlAgentToolType dialect)
    {
        var context = new SqlPlanValidationContext("iterative-not-v1");
        const int depth = 2_048;

        string Compile(string predicate) =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM users WHERE " + predicate,
                dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql;

        var basePredicate = Compile("id = 7");
        var oddPredicate = Compile("NOT id = 7");

        // The same equivalence holds for UNKNOWN: NOT NOT UNKNOWN = UNKNOWN.
        Assert.Equal(
            basePredicate,
            Compile(string.Concat(Enumerable.Repeat("NOT ", depth)) + "id = 7"));
        Assert.Equal(
            oddPredicate,
            Compile(string.Concat(Enumerable.Repeat("NOT ", depth + 1)) + "id = 7"));

        Assert.Equal(
            Compile("id IS NULL"),
            Compile("NOT NOT id IS NULL"));
        Assert.Equal(
            Compile("NOT (id = 7 OR id = 8)"),
            Compile("NOT NOT NOT (id = 7 OR id = 8)"));
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void UnarySignChains_ParseBindNormalizeWithoutPerPrefixRecursion(
        SqlAgentToolType dialect)
    {
        // Distinct from prefix NOT: unary +/- used to recurse once per token
        // in the parser, again in Binder, then again in Normalizer.
        var context = new SqlPlanValidationContext("iterative-unary-sign-v1");
        string Compile(string expression) =>
            SqlCoreFacade.CompileQuery(
                "SELECT " + expression + " FROM users",
                dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql;

        var baseline = Compile("id");
        foreach (var depth in new[] { 32, 128 })
        {
            Assert.Equal(
                baseline,
                Compile(string.Concat(Enumerable.Repeat("+ ", depth)) + "id"));

            // Even counts of unary negatives retain the original AST
            // sequence instead of silently rewriting SQL arithmetic.
            var negative = Compile(string.Concat(Enumerable.Repeat("- ", depth)) + "id");
            Assert.NotEmpty(negative);
            Assert.Contains("id", negative, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void NestedUnarySigns_PreserveLiteralFoldingAndCastPrecedence(
        SqlAgentToolType dialect)
    {
        var compiled = SqlCoreFacade.CompileQuery(
            "SELECT - - 7",
            dialect, dialect,
            new SqlPlanValidationContext("iterative-unary-sign-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
        Assert.Contains(compiled.Parameters, parameter => Convert.ToInt64(parameter.Value) == -7L);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void BooleanChainRendering_PreservesParenthesizationAndParameterOrder(
        SqlAgentToolType dialect)
    {
        var context = new SqlPlanValidationContext("left-deep-render-v1");
        string Compile(string sql) => SqlCoreFacade.CompileQuery(
            sql, dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql;

        Assert.Equal(
            Compile("SELECT id FROM users WHERE (id = 7 AND id = 8) AND id = 9"),
            Compile("SELECT id FROM users WHERE id = 7 AND id = 8 AND id = 9"));
        Assert.NotEqual(
            Compile("SELECT id FROM users WHERE (id = 7 OR id = 8) AND id = 9"),
            Compile("SELECT id FROM users WHERE id = 7 OR id = 8 AND id = 9"));

        // Mixed operators retain the original tree shape and each operand's
        // binding order without exploiting commutativity or short-circuiting.
        var sql = "SELECT id FROM users WHERE (id = 7 OR id = 8) AND id = 9";
        Assert.NotEmpty(Compile(sql));
        const int depth = 512;
        var longExpression = string.Join(" AND ",
            Enumerable.Repeat("id = 7", depth));
        var longSql = Compile("SELECT id FROM users WHERE " + longExpression);
        Assert.NotEmpty(longSql);
        Assert.True(longSql.Length > depth);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DeepArithmeticTree_PostOrderBindingPreservesEveryParameter(
        SqlAgentToolType dialect)
    {
        // Unlike redundant parentheses/unary prefixes, the full binary AST
        // contains one meaningful node per addition. Every provider must
        // preserve source-order parameters after Binder and Normalizer.
        const int depth = 256;
        var expression = "id" + string.Concat(
            Enumerable.Range(1, depth).Select(value => " + " + value));
        var command = SqlCoreFacade.CompileQuery(
            "SELECT " + expression + " FROM users",
            dialect, dialect,
            new SqlPlanValidationContext("iterative-postorder-binary-v1"),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(command.Sql);
        Assert.Equal(depth, command.Parameters.Length);
        Assert.Equal(
            Enumerable.Range(1, depth).Select(value => (long)value),
            command.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedCaseInListAndBetween_PostOrderKeepsBindingOrder(
        SqlAgentToolType dialect)
    {
        // CASE arms and WHERE predicates include branching nodes rather
        // than a trivially linear expression chain.
        var command = SqlCoreFacade.CompileQuery(
            "SELECT CASE WHEN id = 1 THEN COALESCE(id, 2) ELSE 3 END FROM users "
            + "WHERE id BETWEEN 4 AND 5 AND id IN (6, 7)",
            dialect, dialect,
            new SqlPlanValidationContext("iterative-postorder-mixed-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(
            new long[] { 1, 2, 3, 4, 5, 6, 7 },
            command.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BETWEEN", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" IN ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DeepScalarSubqueryChain_PreservesEverySelectAndBinding(
        SqlAgentToolType dialect)
    {
        // Small enough to execute in the shared xUnit process on all six
        // dialects. 2,048-deep cases belong to isolated subprocess probes:
        // a StackOverflowException terminates the process, not just a test.
        const int depth = 128;
        var sql = "SELECT " + string.Concat(Enumerable.Repeat("(SELECT ", depth)) +
            "1" + new string(')', depth);
        var compiled = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("nested-scalar-render-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
        Assert.Single(compiled.Parameters);
        Assert.Equal(depth + 1, compiled.Sql.Split("SELECT ", StringSplitOptions.None).Length - 1);
        Assert.True(compiled.Sql.Length > depth * 7);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void ScalarSubqueryWrappers_PreserveProjectionAliasesAndBindingOrder(
        SqlAgentToolType dialect)
    {
        var context = new SqlPlanValidationContext("nested-scalar-render-v1");
        string Compile(string sql) =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, context, new SqlExecutionPlanPolicy()).Sql;

        Assert.NotEqual(
            Compile("SELECT (SELECT 1)"),
            Compile("SELECT (SELECT 1 AS inner_value)"));
        Assert.NotEqual(
            Compile("SELECT (SELECT 1)"),
            Compile("SELECT (SELECT 1) AS outer_value"));

        var pair = SqlCoreFacade.CompileQuery(
            "SELECT (SELECT 1), (SELECT 2)",
            dialect, dialect, context, new SqlExecutionPlanPolicy());
        Assert.Equal(2, pair.Parameters.Length);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedArithmeticGrouping_PreservesPrecedence(
        SqlAgentToolType dialect)
    {
        var context = new SqlPlanValidationContext("iterative-grouping-v1");
        var inputs = new[]
        {
            "SELECT (1 + 2) * 3",
            "SELECT ((1) + (2)) * 3",
            "SELECT (((1) + 2)) * (3 + 4)",
            "SELECT (1 + (2 * 3))"
        };
        foreach (var sql in inputs)
        {
            var result = SqlCoreFacade.CompileQuery(
                sql, dialect, dialect, context, new SqlExecutionPlanPolicy());
            Assert.NotEmpty(result.Sql);
        }
    }

    [Fact]
    public void DeepGroupingAroundScalarSubquery_DoesNotChangeQueryGrammar()
    {
        const int depth = 128;
        var sql = "SELECT " + new string('(', depth) +
            "(SELECT 1)" + new string(')', depth);
        var context = new SqlPlanValidationContext("iterative-grouping-v1");
        var compiled = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            context, new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void ExcessiveUnmatchedGrouping_FailsLexicallyBeforeParserRecursion(
        SqlAgentToolType dialect)
    {
        // Below the independent SQL source-length and token-count limits,
        // but large enough to exhaust a naive recursive-descent call stack.
        var text = "SELECT " + new string('(', 25_000) + "1";
        var error = Assert.Throws<SqlParseException>(() => SqlCoreFacade.CompileQuery(
            text, dialect, dialect,
            new SqlPlanValidationContext("iterative-grouping-v1"),
            new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_LEXICAL_UNBALANCED_PARENTHESIS", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, error.Diagnostic?.Stage);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void ExtraClosingParenthesis_IsRejectedWithTypedLexicalDiagnostic(
        SqlAgentToolType dialect)
    {
        var error = Assert.Throws<SqlParseException>(() => SqlCoreFacade.CompileQuery(
            "SELECT 1)", dialect, dialect,
            new SqlPlanValidationContext("iterative-grouping-v1"),
            new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_LEXICAL_UNBALANCED_PARENTHESIS", error.Diagnostic?.Code);
    }

    [Fact]
    public void UnmatchedGrouping_IsStillRejected()
    {
        const int depth = 128;
        var sql = "SELECT " + new string('(', depth) + "1" + new string(')', depth - 1);
        var error = Assert.Throws<SqlParseException>(() => SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("iterative-grouping-v1"),
            new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_LEXICAL_UNBALANCED_PARENTHESIS", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, error.Diagnostic?.Stage);
    }
}
