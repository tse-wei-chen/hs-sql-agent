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
    public void DeepNestedSetOperands_ParseOnHeapForAllSixSourceDialects(
        SqlAgentToolType dialect)
    {
        // Parsing must no longer call parseQuery recursively for every
        // parenthesized SET operand. Dialect target capability is a separate
        // concern; this exercise is deliberately limited to the parser API.
        const int depth = 512;
        var sql = "SELECT 1";
        for (var level = 2; level <= depth; level++)
            sql = $"SELECT {level} UNION ({sql})";

        var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
        Assert.NotNull(parsed.Statement);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DeepNestedCteParser_UsesTheQueryHeapScheduler(
        SqlAgentToolType dialect)
    {
        // Every CTE body crosses Query -> WITH body -> Query, not a SET
        // boundary. It must not consume a CLR parseQuery frame per level.
        var sql = "SELECT id FROM users";
        for (var depth = 0; depth < 512; depth++)
            sql = "WITH c AS (" + sql + ") SELECT id FROM c";

        var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
        Assert.NotNull(parsed.Statement);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MultipleCtes_KeepLexicalOrderAndColumnAliasLists(
        SqlAgentToolType dialect)
    {
        const string sql =
            "WITH first_cte (a) AS (SELECT 1), "
            + "second_cte (b) AS (SELECT a FROM first_cte) "
            + "SELECT b FROM second_cte";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("iterative-cte-parse-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("WITH", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Parameters.Length);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DeepDerivedFromChains_ParseWithQueryHeapScheduler(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT id FROM users";
        for (var i = 0; i < 512; i++)
            sql = "SELECT d.id FROM (" + sql + ") AS d";

        var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
        Assert.NotNull(parsed.Statement);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void AlternatingCteAndDerivedQueryBoundaries_ParseFromHeap(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT id FROM users";
        for (var i = 0; i < 512; i++)
            sql = i % 2 == 0
                ? "WITH c AS (" + sql + ") SELECT id FROM c"
                : "SELECT d.id FROM (" + sql + ") AS d";

        var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
        Assert.NotNull(parsed.Statement);
    }

    [Fact]
    public void NestedSetOperand_RejectsLocalOrderByBeforeFollowingIntersect()
    {
        const string sql =
            "SELECT 1 UNION (SELECT 2 ORDER BY 1) INTERSECT SELECT 3";
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres));
        Assert.Contains("ORDER BY/OFFSET", error.Message, StringComparison.Ordinal);
    }

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
    public void RightDeepAndOrRendering_PreservesTreeAndEveryParameter(
        SqlAgentToolType dialect)
    {
        const int depth = 128;
        var predicate = $"id = {depth}";
        for (var index = depth - 1; index >= 1; index--)
        {
            var op = index % 2 == 0 ? " OR " : " AND ";
            predicate = $"(id = {index}{op}{predicate})";
        }

        var command = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users WHERE " + predicate,
            dialect, dialect,
            new SqlPlanValidationContext("iterative-boolean-graph-v1"),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(command.Sql);
        Assert.Equal(depth, command.Parameters.Length);
        Assert.Equal(
            Enumerable.Range(1, depth).Select(value => (long)value),
            command.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
        Assert.True(command.Sql.Count(character => character == '(') >= depth - 1);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void RightBranchBooleanRenderer_DoesNotReassociateAndOr(
        SqlAgentToolType dialect)
    {
        string Compile(string predicate) =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM users WHERE " + predicate,
                dialect, dialect,
                new SqlPlanValidationContext("iterative-boolean-graph-v1"),
                new SqlExecutionPlanPolicy()).Sql;

        Assert.NotEqual(
            Compile("id = 1 OR (id = 2 AND id = 3)"),
            Compile("(id = 1 OR id = 2) AND id = 3"));
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
    public void DeepMixedArithmeticRenderer_RetainsGroupingAndOrderedBindings(
        SqlAgentToolType dialect)
    {
        const int depth = 512;
        var operators = new[] { " + ", " - ", " * ", " / " };
        var expression = "id";
        for (var index = 1; index <= depth; index++)
            expression += operators[(index - 1) % operators.Length] + index;

        var command = SqlCoreFacade.CompileQuery(
            "SELECT " + expression + " FROM users",
            dialect, dialect,
            new SqlPlanValidationContext("iterative-arithmetic-render-v1"),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(command.Sql);
        Assert.Equal(depth, command.Parameters.Length);
        Assert.Equal(
            Enumerable.Range(1, depth).Select(x => (long)x),
            command.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));

        // Every arithmetic node is parenthesized, with no reassociation.
        Assert.True(command.Sql.Count(character => character == '(') >= depth);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedArithmeticRenderer_DoesNotChangeOperatorPrecedence(
        SqlAgentToolType dialect)
    {
        string Compile(string sql) => SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("iterative-arithmetic-render-v1"),
            new SqlExecutionPlanPolicy()).Sql;

        Assert.Equal(
            Compile("SELECT (((id + 1) - 2) * 3) / 4 FROM users"),
            Compile("SELECT (id + 1 - 2) * 3 / 4 FROM users"));
        Assert.NotEqual(
            Compile("SELECT (id + 1) * 2 FROM users"),
            Compile("SELECT id + 1 * 2 FROM users"));
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
    public void OrderingCapabilityProof_WithDeepPredicate_RemainsStackIndependent(
        SqlAgentToolType dialect)
    {
        // The capability proof must visit WHERE even when no ORDER BY exists.
        // A deep predicate previously exhausted a 256 KiB execution stack in
        // proveOrderingExpr rather than rejecting safely.
        var sql = "SELECT id FROM users WHERE "
            + string.Join(" AND ", Enumerable.Repeat("id = 7", 128));
        var command = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("iterative-ordering-proof-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
        Assert.Equal(128, command.Parameters.Length);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedQueryGraph_ExistsDerivedSetAndCorrelatedPredicate_KeepsParameterOrder(
        SqlAgentToolType dialect)
    {
        // Exercise the full Expr -> Query -> Select -> Source -> Query ->
        // SetOperation path, including a correlated outer-row reference.
        var sql = "SELECT u.id FROM users AS u WHERE EXISTS ("
            + "SELECT d.id FROM ("
            + "SELECT id FROM archived WHERE id = 1 "
            + "UNION ALL SELECT id FROM old_archive WHERE id = 2"
            + ") AS d WHERE d.id = u.id) AND u.id > 3";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("iterative-mixed-query-graph-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("EXISTS", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION ALL", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new long[] { 1, 2, 3 },
            result.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedQueryGraph_CteAliasIsLoweredBeforeDependentProjection(
        SqlAgentToolType dialect)
    {
        var sql = "WITH scoped_rows (row_key) AS ("
            + "SELECT id FROM archived WHERE id = 7) "
            + "SELECT row_key FROM scoped_rows WHERE row_key > 9";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("iterative-mixed-query-graph-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("WITH", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("row_key", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new long[] { 7, 9 },
            result.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedQueryGraph_SetBranchProjectionWidthMismatch_RemainsFailClosed(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT id FROM users WHERE EXISTS ("
            + "SELECT id FROM ("
            + "SELECT id FROM archived UNION ALL "
            + "SELECT id, name FROM old_archive) AS d)";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect,
                new SqlPlanValidationContext("iterative-mixed-query-graph-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("projection width", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedQueryGraph_CteAliasWidthMismatch_RemainsFailClosed(
        SqlAgentToolType dialect)
    {
        var sql = "WITH scoped_rows (first_id, second_id) AS ("
            + "SELECT id FROM archived) SELECT first_id FROM scoped_rows";
        var error = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect,
                new SqlPlanValidationContext("iterative-mixed-query-graph-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("column alias", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedBinderGraph_CorrelatedInAndExistsAcrossDerivedSetBranches(
        SqlAgentToolType dialect)
    {
        // A source-aware Binder must carry the outer row scope through IN,
        // derived UNION branches, JOIN-like row sources and EXISTS without
        // a recursive Expr -> Query callback.
        var sql = "SELECT u.id FROM users AS u WHERE u.id IN ("
            + "SELECT d.id FROM (SELECT id FROM archived WHERE id = 1 "
            + "UNION ALL SELECT id FROM old_archive WHERE id = 2) AS d "
            + "WHERE d.id = u.id) AND EXISTS ("
            + "SELECT a.id FROM accounts AS a WHERE a.id = u.id) "
            + "AND u.id > 3";
        var command = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("scope-aware-mixed-binder-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new long[] { 1, 2, 3 },
            command.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedBinderGraph_CteVisibilityAndCorrelatedNestedPredicate(
        SqlAgentToolType dialect)
    {
        var sql = "WITH scoped AS (SELECT id FROM users WHERE id = 11) "
            + "SELECT s.id FROM scoped AS s WHERE EXISTS ("
            + "SELECT o.id FROM orders AS o WHERE o.id = s.id)";
        var command = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("scope-aware-mixed-binder-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(11L, Convert.ToInt64(command.Parameters[0].Value));
    }

    [Fact]
    public void MixedJoinCapabilityProof_CteDerivedLateralAndSetPreserveCorrelatedBindings()
    {
        // Deep-enough mixed boundaries to exercise source-first JOIN proofs:
        // CTE -> JOIN/LATERAL -> derived SET -> correlated predicate.
        var query = "SELECT id FROM archived WHERE id = 1 UNION ALL "
            + "SELECT id FROM old_archive WHERE id = 2";
        var sql = "WITH scoped AS (SELECT id FROM users) "
            + "SELECT s.id, x.id FROM scoped AS s "
            + "CROSS JOIN LATERAL (SELECT z.id FROM (" + query
            + ") AS z WHERE z.id = s.id) AS x "
            + "WHERE s.id > 3";
        var result = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("mixed-join-query-proof-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("LATERAL", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION ALL", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new long[] { 1, 2, 3 },
            result.Parameters.Select(value => Convert.ToInt64(value.Value)));
    }

    [Fact]
    public void MixedJoinCapabilityProof_DeepDerivedAndCteAlternationPreservesSelection()
    {
        // Every nested SELECT crosses a different Query/Source edge; the
        // JOIN capability pass must not consume a CLR call per crossing.
        var sql = "SELECT id FROM users";
        for (var depth = 0; depth < 96; depth++)
            sql = depth % 2 == 0
                ? $"SELECT d.id FROM ({sql}) AS d"
                : $"WITH c AS ({sql}) SELECT id FROM c";

        var result = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("mixed-join-query-proof-v1"),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(result.Sql);
    }

    [Fact]
    public void DeepFilterAggregatePredicate_ValidatesAndRendersWithoutRecursiveProof()
    {
        // FILTER predicate capability proof must walk a left-deep Boolean
        // chain on heap frames. The rest of compilation and rendering still
        // validate the exact Postgres SQL semantics and parameter order.
        var predicate = string.Join(" AND ",
            Enumerable.Repeat("id = 7", 512));
        var sql = "SELECT SUM(id) FILTER (WHERE " + predicate + ") FROM users";
        var command = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("iterative-filter-predicate-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("FILTER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(512, command.Parameters.Length);
    }

    [Fact]
    public void MixedBinderGraph_LateralJoinOnlySeesPrecedingSources()
    {
        var sql = "SELECT u.id, q.id, r.id FROM users AS u "
            + "CROSS JOIN LATERAL (SELECT u.id AS id) AS q "
            + "LEFT JOIN LATERAL (SELECT q.id AS id) AS r ON r.id = q.id";
        var command = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("scope-aware-mixed-binder-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("CROSS JOIN LATERAL", command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEFT JOIN LATERAL", command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedBinderGraph_NonLateralSourceCannotSeePrecedingJoin()
    {
        var sql = "SELECT u.id FROM users AS u "
            + "CROSS JOIN LATERAL (SELECT u.id AS id) AS q "
            + "CROSS JOIN (SELECT q.id AS id) AS r";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var error = Assert.Throws<InvalidOperationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("scope-aware-mixed-binder-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("unknown table/alias qualifier", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.Data["HsSqlAgent.SqlCore.Diagnostic"]);
    }

    [Fact]
    public void MixedBinderGraph_FirstInvalidQualifierInProjectionWins()
    {
        var sql = "SELECT missing_outer.id, "
            + "(SELECT missing_inner.id FROM archived) FROM users AS u";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var error = Assert.Throws<InvalidOperationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("scope-aware-mixed-binder-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("missing_outer", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing_inner", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedBinderGraph_InvalidQualifierInsideEarlierSubqueryWins()
    {
        var sql = "SELECT (SELECT missing_inner.id FROM archived), "
            + "missing_outer.id FROM users AS u";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var error = Assert.Throws<InvalidOperationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("scope-aware-mixed-binder-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("missing_inner", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing_outer", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void WideSelectJoinsAndWhere_KeepSourceOrderedParameters(
        SqlAgentToolType dialect)
    {
        const int joinCount = 32;
        var clauses = Enumerable.Range(1, joinCount).Select(index =>
            $"INNER JOIN parts AS p{index} ON p{index}.id = {index}");
        var sql = "SELECT u.id FROM users AS u "
            + string.Join(" ", clauses) + " WHERE u.id = 99";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("streaming-wide-select-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(joinCount,
            result.Sql.Split("INNER JOIN", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            Enumerable.Range(1, joinCount).Select(x => (long)x).Append(99L),
            result.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void WideUnionBranches_KeepSetOperatorAndParameterOrder(
        SqlAgentToolType dialect)
    {
        const int branchCount = 48;
        var branches = Enumerable.Range(1, branchCount)
            .Select(index => $"SELECT id FROM users WHERE id = {index}");
        var sql = string.Join(" UNION ALL ", branches);
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("streaming-wide-set-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(branchCount - 1,
            result.Sql.Split("UNION ALL", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            Enumerable.Range(1, branchCount).Select(x => (long)x),
            result.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void NestedExistsWhereQueryStream_PreservesOuterToInnerParameterOrder(
        SqlAgentToolType dialect)
    {
        // Every level has one bound projection literal. The iterative
        // renderer must bind them outer-to-inner, not render the innermost
        // SQL first or materialize O(depth) intermediate query strings.
        const int depth = 128;
        var prefixes = Enumerable.Range(1, depth)
            .Select(index => $"SELECT {index} WHERE EXISTS (");
        var sql = "SELECT u.id FROM users u WHERE EXISTS ("
            + string.Concat(prefixes)
            + "SELECT u.id" + new string(')', depth + 1);

        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("streaming-exists-query-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(
            Enumerable.Range(1, depth).Select(i => (long)i),
            result.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Equal(depth + 1,
            result.Sql.Split("EXISTS (", StringSplitOptions.None).Length - 1);
        if (dialect == SqlAgentToolType.Oracle)
            Assert.Contains("FROM DUAL", result.Sql, StringComparison.Ordinal);
        if (dialect == SqlAgentToolType.Firebird)
            Assert.Contains("FROM RDB$DATABASE", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void NestedExistsWhereQueryStream_StopsAtNonSimpleInnerSelect(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT u.id FROM users u WHERE EXISTS ("
            + "SELECT 1 WHERE EXISTS ("
            + "SELECT 2 WHERE EXISTS ("
            + "SELECT a.id FROM archived AS a WHERE a.id = u.id AND a.id > 3)))";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("streaming-exists-query-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(new long[] { 1, 2, 3 },
            result.Parameters.Select(p => Convert.ToInt64(p.Value)));
        Assert.Contains("archived", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void MixedBinderGraph_AlternatingExistsDepthPreservesCorrelatedScope(
        SqlAgentToolType dialect)
    {
        // Alternates Query and Expr/WHERE with no pure-scalar-chain
        // shortcut. The Binder must retain the outer user alias through
        // every independently scoped EXISTS predicate.
        const int depth = 48;
        var sql = "SELECT u.id FROM users u WHERE EXISTS ("
            + string.Concat(Enumerable.Repeat("SELECT 1 WHERE EXISTS (", depth))
            + "SELECT u.id" + new string(')', depth + 1);
        var command = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("mixed-binder-depth-v1"),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(command.Sql);
        Assert.Equal(depth + 1,
            command.Sql.Split("EXISTS (", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void NestedCteStream_PreservesInnermostFirstParameterBindings()
    {
        var sql = "WITH outer_cte AS (" +
                  "WITH inner_cte AS (SELECT 11 AS a, 22 AS b) " +
                  "SELECT a FROM inner_cte WHERE b = 33" +
                  ") SELECT a FROM outer_cte WHERE a = 44";
        var command = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("nested-cte-parameter-order-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Equal(4, command.Parameters.Count());
        Assert.Equal(new long[] { 11, 22, 33, 44 },
            command.Parameters.Select(item => Convert.ToInt64(item.Value)).ToArray());
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DerivedSourceWithWhere_StreamsWithInnermostFirstBindings(
        SqlAgentToolType dialect)
    {
        var query = "SELECT id FROM users";
        for (var i = 1; i <= 64; i++)
            query = $"SELECT d.id FROM ({query}) d WHERE d.id > {i}";
        var compiled = SqlCoreFacade.CompileQuery(
            query, dialect, dialect,
            new SqlPlanValidationContext("derived-where-stream-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Equal(64, compiled.Sql.Split(" WHERE ", StringSplitOptions.None).Length - 1);
        Assert.Equal(Enumerable.Range(1, 64).Select(i => (long)i),
            compiled.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
    }

    [Fact]
    public void PostgresDerivedPagingChain_StreamsSuffixesInParameterOrder()
    {
        var query = "SELECT id FROM users";
        for (var i = 1; i <= 32; i++)
            query = $"SELECT d.id FROM ({query}) d WHERE d.id >= {i} " +
                    $"ORDER BY d.id LIMIT {i + 1}";
        var compiled = SqlCoreFacade.CompileQuery(
            query, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("derived-paging-stream-v1"),
            new SqlExecutionPlanPolicy());
        var expected = Enumerable.Range(1, 32)
            .SelectMany(i => new long[] { i, i + 1 });
        Assert.Equal(expected, compiled.Parameters.Select(x => Convert.ToInt64(x.Value)));
        Assert.Equal(32, compiled.Sql.Split(" LIMIT ", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void PostgresNestedSetBranches_StreamWithStableParameterOrder()
    {
        const int depth = 64;
        var sql = "SELECT id FROM users WHERE id = 1";
        for (var i = 2; i <= depth; i++)
            sql = $"SELECT id FROM users WHERE id = {i} UNION ({sql})";

        var compiled = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("nested-set-stream-v1"),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(compiled.Sql);
        Assert.Equal(depth - 1,
            compiled.Sql.Split(" UNION ", StringSplitOptions.None).Length - 1);
        Assert.Equal(Enumerable.Range(1, depth).Reverse().Select(i => (long)i),
            compiled.Parameters.Select(value => Convert.ToInt64(value.Value)));
    }

    [Fact]
    public void DeepNestedCte_StreamsIntoSingleBoundedEmitter()
    {
        const int depth = 128;
        var query = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            query = $"WITH c AS ({query}) SELECT id FROM c";
        var compiled = SqlCoreFacade.CompileQuery(
            query, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("nested-cte-render-v1"),
            new SqlExecutionPlanPolicy());

        Assert.NotEmpty(compiled.Sql);
        Assert.Equal(depth,
            compiled.Sql.Split("WITH ", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DerivedTableChain_StreamsWithoutRecursiveQueryMaterialization(
        SqlAgentToolType dialect)
    {
        const int depth = 128;
        var query = "SELECT id FROM users";
        for (var i = 0; i < depth; i++)
            query = $"SELECT d.id FROM ({query}) d";
        var compiled = SqlCoreFacade.CompileQuery(
            query, dialect, dialect,
            new SqlPlanValidationContext("derived-render-chain-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
        Assert.Equal(depth,
            compiled.Sql.Split(" FROM (", StringSplitOptions.None).Length - 1);
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
    public void DeepPureScalarChain_PreservesCorrelatedParentScopeAcrossQueries(
        SqlAgentToolType dialect)
    {
        const int depth = 96;
        var sql = "SELECT u.id, "
            + string.Concat(Enumerable.Repeat("(SELECT ", depth))
            + "u.id" + new string(')', depth)
            + " AS echoed FROM users AS u WHERE u.id = 9";

        var compiled = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("iterative-cross-query-scope-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(depth + 1, compiled.Sql.Split("SELECT ", StringSplitOptions.None).Length - 1);
        Assert.Single(compiled.Parameters);
        Assert.Equal(9L, Convert.ToInt64(compiled.Parameters[0].Value));
        Assert.Contains("echoed", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void DeepPureScalarChain_UnknownQualifiedAncestorStillFailsClosed(
        SqlAgentToolType dialect)
    {
        const int depth = 48;
        var sql = "SELECT u.id, "
            + string.Concat(Enumerable.Repeat("(SELECT ", depth))
            + "missing.id" + new string(')', depth)
            + " FROM users AS u";
        var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
        var error = Assert.Throws<InvalidOperationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed, dialect,
                new SqlPlanValidationContext("iterative-cross-query-scope-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("unknown table/alias qualifier", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.Data["HsSqlAgent.SqlCore.Diagnostic"]);
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
    public void DeepRightNestedArithmeticAndUnary_PreservesBindingOrder(
        SqlAgentToolType dialect)
    {
        // Previously the left-spine renderer missed right-deep arithmetic
        // and each nested unary entered renderExpr recursively. Exercise
        // both forms together without reassociating subtraction/negation.
        const int depth = 128;
        var expression = $"id + {depth}";
        for (var index = depth - 1; index >= 1; index--)
            expression = $"id + (-({expression}) + {index})";

        var result = SqlCoreFacade.CompileQuery(
            "SELECT " + expression + " FROM users",
            dialect, dialect,
            new SqlPlanValidationContext("iterative-scalar-render-graph-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(depth, result.Parameters.Length);
        Assert.Equal(
            Enumerable.Range(1, depth).Reverse().Select(x => (long)x),
            result.Parameters.Select(parameter => Convert.ToInt64(parameter.Value)));
        Assert.True(result.Sql.Count(character => character == '(') >= depth * 2);
        Assert.Contains("(-", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SourceDialects))]
    public void ScalarRenderGraph_ComposesBetweenInListAndLikeWithoutReordering(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT id FROM users WHERE "
            + "(id + 1) BETWEEN 2 AND (3 + id) "
            + "AND id IN (4, 5 + 6) AND name LIKE 'a%'";
        var result = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("iterative-scalar-render-graph-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(7, result.Parameters.Length);
        Assert.Equal(
            new long[] { 1, 2, 3, 4, 5, 6 },
            result.Parameters.Take(6).Select(p => Convert.ToInt64(p.Value)));
        Assert.Equal("a%", Convert.ToString(result.Parameters[6].Value));
        Assert.Contains("BETWEEN", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" IN ", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIKE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScalarRenderGraph_PostgresJsonSelectorBindingOrderRemainsStable()
    {
        // Postgres JSON selectors are bound from outer node to inner node
        // in the existing renderer, *before* its left expression is visited.
        var sql = "SELECT (payload -> 'root') ->> 'name' "
            + "FROM events WHERE id = 7";
        var result = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("iterative-scalar-render-graph-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(
            new[] { "name", "root", "7" },
            result.Parameters.Select(p => Convert.ToString(p.Value)));
        Assert.Contains("->>", result.Sql, StringComparison.Ordinal);
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
    public void UnbalancedAndOverBudgetGrouping_PreservesSyntaxDiagnosticPrecedence(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT " + new string('(', 8_193) + "1";
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect,
                new SqlPlanValidationContext("nesting-budget-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Equal("SQL_LEXICAL_UNBALANCED_PARENTHESIS", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticCategory.Syntax, error.Diagnostic?.Category);
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
