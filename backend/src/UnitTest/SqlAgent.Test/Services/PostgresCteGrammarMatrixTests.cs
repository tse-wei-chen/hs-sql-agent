using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class PostgresCteGrammarMatrixTests
{
    private static readonly (string Name, string Sql, string RenderedFragment, string TablesCsv)[] Bodies =
    [
        ("plain", "SELECT id FROM users", "FROM users", "users"),
        ("where-ilike", "SELECT id FROM users WHERE name ILIKE 'a%'", "ILIKE", "users"),
        ("join-on", "SELECT u.id FROM users u JOIN accounts a ON a.user_id = u.id", " JOIN ", "users,accounts"),
        ("join-using", "SELECT id FROM users JOIN accounts USING (id)", "USING (", "users,accounts"),
        ("group-having", "SELECT user_id AS id FROM orders GROUP BY user_id HAVING COUNT(*) > 0", "HAVING", "orders"),
        ("window", "SELECT id, LAG(id) OVER (ORDER BY id) AS previous_id FROM users", " OVER (", "users"),
        ("filter", "SELECT user_id AS id, SUM(amount) FILTER (WHERE status = 'open') AS total FROM orders GROUP BY user_id", "FILTER (WHERE", "orders"),
        ("subquery", "SELECT id FROM users WHERE id IN (SELECT user_id FROM orders)", " IN (", "users,orders"),
        ("set-operation", "SELECT id FROM users UNION SELECT user_id AS id FROM orders", "UNION", "users,orders"),
        ("order-limit-offset", "SELECT id FROM users ORDER BY id LIMIT 10 OFFSET 1", "LIMIT", "users"),
        ("postfix-cast", "SELECT id FROM events WHERE created_at::date >= DATE '2026-01-01'", "CAST", "events"),
        ("interval", "SELECT id FROM events WHERE created_at >= CURRENT_TIMESTAMP - INTERVAL '1 day'", "INTERVAL", "events")
    ];

    private static readonly string[] RootShapes =
    [
        "simple",
        "column-alias",
        "multiple",
        "root-union",
        "nested-cte",
        "subquery-reference",
        "physical-table-join",
        "quoted-identifier",
        "root-order-limit"
    ];

    public static IEnumerable<object[]> CteGrammarMatrix()
    {
        foreach (var body in Bodies)
        {
            foreach (var rootShape in RootShapes)
            {
                var sql = Wrap(rootShape, body.Sql);
                var expectedTables = rootShape == "physical-table-join"
                    ? MergeTables(body.TablesCsv, "audit_log")
                    : body.TablesCsv;

                yield return
                [
                    $"{rootShape}/{body.Name}",
                    rootShape,
                    body.Name,
                    sql,
                    body.RenderedFragment,
                    expectedTables
                ];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CteGrammarMatrix))]
    public void NonRecursiveCteGrammarMatrix_PreservesAstFactsAndRenderedSemantics(
        string name,
        string rootShape,
        string bodyShape,
        string sql,
        string expectedBodyRenderedFragment,
        string expectedTablesCsv)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var root = Head(parsed.Statement);

        AssertRootShape(parsed.Statement, root, rootShape);
        var effectiveBody = EffectiveBody(root, rootShape);
        AssertBodyShape(effectiveBody, bodyShape);

        var facts = SqlCoreInspection.GetQueryFacts(parsed);
        var expectedTables = expectedTablesCsv.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(expectedTables.Length, facts.ReferencedTables.Count);
        foreach (var table in expectedTables)
        {
            Assert.Contains(
                facts.ReferencedTables,
                actual => string.Equals(actual, table, StringComparison.OrdinalIgnoreCase));
        }

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-cte-grammar-matrix-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql), name);
        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedBodyRenderedFragment, command.Sql, StringComparison.OrdinalIgnoreCase);

        if (rootShape == "quoted-identifier")
            Assert.Contains("\"CaseCte\"", command.Sql, StringComparison.Ordinal);
        if (rootShape == "root-union")
            Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static string Wrap(string rootShape, string body) =>
        rootShape switch
        {
            "simple" => $"WITH x AS ({body}) SELECT id FROM x",
            "column-alias" => $"WITH x(id) AS ({body}) SELECT id FROM x",
            "multiple" => $"WITH x AS ({body}), y AS (SELECT id FROM x) SELECT id FROM y",
            "root-union" => $"WITH x AS ({body}) SELECT id FROM x UNION ALL SELECT id FROM x",
            "nested-cte" => $"WITH x AS (WITH y AS ({body}) SELECT id FROM y) SELECT id FROM x",
            "subquery-reference" => $"WITH x AS ({body}) SELECT q.id FROM (SELECT id FROM x) q",
            "physical-table-join" => $"WITH x AS ({body}) SELECT x.id FROM x JOIN audit_log a ON a.id = x.id",
            "quoted-identifier" => $"WITH \"CaseCte\" AS ({body}) SELECT id FROM \"CaseCte\"",
            "root-order-limit" => $"WITH x AS ({body}) SELECT id FROM x ORDER BY id LIMIT 3 OFFSET 1",
            _ => throw new ArgumentOutOfRangeException(nameof(rootShape), rootShape, null)
        };

    private static string MergeTables(string tablesCsv, string extra) =>
        string.Join(
            ",",
            tablesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(extra)
                .Distinct(StringComparer.OrdinalIgnoreCase));

    private static SelectStatement Head(SqlStatement statement) =>
        statement switch
        {
            SelectStatement select => select,
            QueryStatement query => query.Head,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected query/select AST, got {statement.GetType().Name}.")
        };

    private static SqlStatement EffectiveBody(SelectStatement root, string rootShape)
    {
        var outer = Assert.Single(root.Ctes);
        if (rootShape == "multiple")
            outer = root.Ctes[0];

        if (rootShape != "nested-cte")
            return outer.Query;

        var nestedHead = Head(outer.Query);
        var inner = Assert.Single(nestedHead.Ctes);
        return inner.Query;
    }

    private static void AssertRootShape(
        SqlStatement statement,
        SelectStatement root,
        string rootShape)
    {
        switch (rootShape)
        {
            case "simple":
                Assert.Single(root.Ctes);
                break;
            case "column-alias":
                Assert.Single(root.Ctes);
                Assert.Single(root.Ctes[0].ColumnAliases);
                break;
            case "multiple":
                Assert.Equal(2, root.Ctes.Length);
                break;
            case "root-union":
                Assert.Single(root.Ctes);
                var setQuery = Assert.IsType<QueryStatement>(statement);
                Assert.Single(setQuery.SetOperations);
                Assert.Equal(SetOperationKind.UnionAll, setQuery.SetOperations[0].Kind);
                break;
            case "nested-cte":
                Assert.Single(root.Ctes);
                Assert.Single(Head(root.Ctes[0].Query).Ctes);
                break;
            case "subquery-reference":
                Assert.Single(root.Ctes);
                Assert.IsType<DerivedTableSource>(root.From);
                break;
            case "physical-table-join":
                Assert.Single(root.Ctes);
                Assert.Single(root.Joins);
                Assert.NotNull(root.Joins[0].Predicate);
                break;
            case "quoted-identifier":
                Assert.Single(root.Ctes);
                var part = Assert.Single(root.Ctes[0].Name.Parts);
                Assert.True(part.WasQuoted);
                Assert.Equal("CaseCte", part.Value);
                break;
            case "root-order-limit":
                Assert.Single(root.Ctes);
                Assert.NotEmpty(root.OrderBy);
                Assert.Equal(3, root.Limit);
                Assert.Equal(1, root.Offset);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rootShape), rootShape, null);
        }
    }

    private static void AssertBodyShape(SqlStatement body, string bodyShape)
    {
        var head = Head(body);

        switch (bodyShape)
        {
            case "plain":
                Assert.NotNull(head.From);
                break;
            case "where-ilike":
                Assert.NotNull(head.Where);
                break;
            case "join-on":
                Assert.Single(head.Joins);
                Assert.NotNull(head.Joins[0].Predicate);
                Assert.True(head.Joins[0].UsingColumns.IsDefaultOrEmpty);
                break;
            case "join-using":
                Assert.Single(head.Joins);
                Assert.Null(head.Joins[0].Predicate);
                Assert.Single(head.Joins[0].UsingColumns);
                break;
            case "group-having":
                Assert.NotEmpty(head.GroupBy);
                Assert.NotNull(head.Having);
                break;
            case "window":
                Assert.Contains(head.Select, item => ContainsNode<WindowedExpr>(item.Expression));
                break;
            case "filter":
                Assert.Contains(head.Select, item => ContainsNode<FilterExpr>(item.Expression));
                break;
            case "subquery":
                Assert.True(ContainsNode<SubqueryExpr>(head.Where));
                break;
            case "set-operation":
                var setQuery = Assert.IsType<QueryStatement>(body);
                Assert.Single(setQuery.SetOperations);
                Assert.Equal(SetOperationKind.Union, setQuery.SetOperations[0].Kind);
                break;
            case "order-limit-offset":
                Assert.True(HasOrder(body));
                Assert.Equal(10, Limit(body));
                Assert.Equal(1, Offset(body));
                break;
            case "postfix-cast":
                Assert.True(ContainsNode<CastExpr>(head.Where));
                break;
            case "interval":
                Assert.True(ContainsNode<IntervalExpr>(head.Where));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bodyShape), bodyShape, null);
        }
    }

    private static bool HasOrder(SqlStatement statement) =>
        statement switch
        {
            SelectStatement select => !select.OrderBy.IsDefaultOrEmpty,
            QueryStatement query => !query.OrderBy.IsDefaultOrEmpty || !query.Head.OrderBy.IsDefaultOrEmpty,
            _ => false
        };

    private static int? Limit(SqlStatement statement) =>
        statement switch
        {
            SelectStatement select => select.Limit,
            QueryStatement query when query.Limit.HasValue => query.Limit,
            QueryStatement query => query.Head.Limit,
            _ => null
        };

    private static int? Offset(SqlStatement statement) =>
        statement switch
        {
            SelectStatement select => select.Offset,
            QueryStatement query when query.Offset.HasValue => query.Offset,
            QueryStatement query => query.Head.Offset,
            _ => null
        };

    private static bool ContainsNode<T>(SqlExpr? expression)
        where T : SqlExpr
    {
        if (expression is null)
            return false;
        if (expression is T)
            return true;

        return expression switch
        {
            UnaryExpr unary => ContainsNode<T>(unary.Operand),
            BinaryExpr binary => ContainsNode<T>(binary.Left) || ContainsNode<T>(binary.Right),
            FunctionCallExpr function => function.Arguments.Any(ContainsNode<T>),
            FilterExpr filter => ContainsNode<T>(filter.Expression) || ContainsNode<T>(filter.Predicate),
            WindowedExpr windowed => ContainsNode<T>(windowed.Expression)
                || windowed.Window.PartitionBy.Any(ContainsNode<T>)
                || windowed.Window.OrderBy.Any(item => ContainsNode<T>(item.Expression)),
            CastExpr cast => ContainsNode<T>(cast.Expression),
            ExtractExpr extract => ContainsNode<T>(extract.Expression),
            InExpr @in => ContainsNode<T>(@in.Value) || @in.Items.Any(ContainsNode<T>),
            BetweenExpr between => ContainsNode<T>(between.Value)
                || ContainsNode<T>(between.Lower)
                || ContainsNode<T>(between.Upper),
            IsNullExpr isNull => ContainsNode<T>(isNull.Value),
            _ => false
        };
    }
}
