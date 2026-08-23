using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlTextParserTests
{
    [Fact]
    public void ParseQuery_BuildsCoreAstWithRealSourceSpansAndQuoteIntent()
    {
        const string sql = "SELECT \"UserId\" FROM \"App\".\"Users\" WHERE \"UserId\" = 7";

        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var projection = Assert.IsType<ColumnExpr>(Assert.Single(select.Select).Expression);
        var source = Assert.IsType<NamedTableSource>(select.From);

        Assert.Equal(0, select.Span.Start);
        Assert.Equal(sql.Length, select.Span.End);
        Assert.True(projection.Name.Parts[0].WasQuoted);
        Assert.Equal("UserId", projection.Name.Parts[0].Value);
        Assert.All(source.Name.Parts, part => Assert.True(part.WasQuoted));
        Assert.Equal(["App", "Users"], source.Name.Parts.Select(part => part.Value).ToArray());
        Assert.True(source.Name.Span.Start >= sql.IndexOf("\"App\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseQuery_InIdentifier_PreservesColumnSemanticsInsteadOfCoercingLiteral()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users WHERE id IN (other_id)",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var predicate = Assert.IsType<InExpr>(select.Where);

        Assert.IsType<ColumnExpr>(predicate.Value);
        var item = Assert.IsType<ColumnExpr>(Assert.Single(predicate.Items));
        Assert.Equal("other_id", Assert.Single(item.Name.Parts).Value);
    }

    [Fact]
    public void ParseQuery_CteColumnAliases_AreModeledInsteadOfDiscarded()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH recent(id) AS (SELECT id FROM orders) SELECT id FROM recent",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var cte = Assert.Single(select.Ctes);

        Assert.Equal("recent", Assert.Single(cte.Name.Parts).Value);
        Assert.Equal("id", Assert.Single(Assert.Single(cte.ColumnAliases).Parts).Value);
        Assert.NotEqual(SourceSpan.Unknown, cte.Span);
    }

    [Fact]
    public void ParseQuery_RecursiveCte_FailsClosedUntilRecursiveSemanticsAreModeled()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x",
                SqlAgentToolType.Postgres));

        Assert.Contains("RECURSIVE", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_CommaFrom_IsNormalizedToExplicitCrossJoin()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT a.id FROM a, b WHERE a.id = b.id",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal("a", Assert.Single(Assert.IsType<NamedTableSource>(select.From).Name.Parts).Value);
        var join = Assert.Single(select.Joins);
        Assert.Equal("CROSS", join.Kind);
        Assert.Null(join.Predicate);
        Assert.Equal("b", Assert.Single(Assert.IsType<NamedTableSource>(join.Source).Name.Parts).Value);
    }

    [Fact]
    public void ParseQuery_ComplexExpression_BindsAndCompilesWithoutTransportDto()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT SUM(amount) FILTER (WHERE status = 'open') OVER (PARTITION BY customer_id ORDER BY created_at ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders WHERE id BETWEEN 1 AND 9",
            SqlAgentToolType.Postgres);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OVER (PARTITION BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "open"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 9));
    }

    [Fact]
    public void ParseDml_UpdateBuildsCorePredicateDirectly()
    {
        const string sql = "UPDATE users SET status = 'disabled' WHERE (id = 7 OR owner_id = other_id) AND deleted_at IS NULL";

        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        var root = Assert.IsType<BinaryExpr>(update.Predicate);

        Assert.Equal("AND", root.Operator);
        Assert.IsType<BinaryExpr>(root.Left);
        Assert.IsType<IsNullExpr>(root.Right);
        Assert.Equal(0, update.Span.Start);
        Assert.Equal(sql.Length, update.Span.End);
    }

    [Fact]
    public void ParseDml_WhereSubqueryUsesSameCoreQueryParser()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id IN (SELECT user_id FROM blocked_users WHERE active = TRUE)",
            SqlAgentToolType.Postgres);
        var delete = Assert.IsType<DeleteStatement>(parsed.Statement);
        var predicate = Assert.IsType<BinaryExpr>(delete.Predicate);

        Assert.Equal("IN", predicate.Operator);
        Assert.IsType<SubqueryExpr>(predicate.Right);
    }

    [Fact]
    public void ParseDml_UnboundParameterStillFailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "UPDATE users SET name = :name WHERE id = 1",
                SqlAgentToolType.Postgres));

        Assert.Contains("Unbound SQL parameter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_ProducesFactsFromCoreBinderOnly()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH x AS (SELECT id FROM audit.events) SELECT x.id FROM x JOIN crm.users u ON x.id = u.id",
            SqlAgentToolType.Postgres);

        var bound = new SqlAstBinder().Bind(parsed);

        Assert.Contains("audit.events", bound.Facts.ReferencedTables);
        Assert.Contains("crm.users", bound.Facts.ReferencedTables);
        Assert.DoesNotContain("x", bound.Facts.ReferencedTables);
    }
}
