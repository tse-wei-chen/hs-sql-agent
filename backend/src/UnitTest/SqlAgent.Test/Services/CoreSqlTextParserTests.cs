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
    }

    [Fact]
    public void ParseQuery_PreservesProjectionAndTableAliasQuoteIntent()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id AS \"DisplayName\" FROM users AS \"UserScope\"",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var projectionAlias = Assert.Single(select.Select).Alias;
        var tableAlias = Assert.IsType<NamedTableSource>(select.From).Alias;

        Assert.NotNull(projectionAlias);
        Assert.Equal("DisplayName", projectionAlias.Value);
        Assert.True(projectionAlias.WasQuoted);
        Assert.NotNull(tableAlias);
        Assert.Equal("UserScope", tableAlias.Value);
        Assert.True(tableAlias.WasQuoted);
    }

    [Fact]
    public void Compile_PostgresAliases_PreserveQuotedCaseAndFoldUnquotedCase()
    {
        var quoted = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT id AS \"DisplayName\" FROM users AS \"UserScope\"",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("\"DisplayName\"", quoted.Sql, StringComparison.Ordinal);
        Assert.Contains("\"UserScope\"", quoted.Sql, StringComparison.Ordinal);

        var unquoted = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT id AS DisplayName FROM users AS UserScope",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("\"displayname\"", unquoted.Sql, StringComparison.Ordinal);
        Assert.Contains("\"userscope\"", unquoted.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DisplayName\"", unquoted.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"UserScope\"", unquoted.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_PostgresDerivedAlias_PreservesQuotedCase()
    {
        var command = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT \"DerivedScope\".id FROM (SELECT id FROM users) AS \"DerivedScope\"",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("\"DerivedScope\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseQuery_InIdentifier_PreservesColumnSemanticsInsteadOfCoercingLiteral()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users WHERE id IN (other_id)",
            SqlAgentToolType.Postgres);
        var predicate = Assert.IsType<InExpr>(Assert.IsType<SelectStatement>(parsed.Statement).Where);
        Assert.IsType<ColumnExpr>(predicate.Value);
        var item = Assert.IsType<ColumnExpr>(Assert.Single(predicate.Items));
        Assert.Equal("other_id", Assert.Single(item.Name.Parts).Value);
    }

    [Fact]
    public void ParseQuery_DateNamedColumn_IsNotMisreadAsTypedLiteral()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT date FROM events", SqlAgentToolType.Postgres);
        var expression = Assert.IsType<ColumnExpr>(Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);
        Assert.Equal("date", Assert.Single(expression.Name.Parts).Value, ignoreCase: true);
    }

    [Fact]
    public void ParseQuery_DateFunction_IsParsedAsFunctionInsteadOfTypedLiteral()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT DATE(created_at) FROM events", SqlAgentToolType.MySQL);
        var expression = Assert.IsType<FunctionCallExpr>(Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);
        Assert.Equal("DATE", Assert.Single(expression.Name.Parts).Value, ignoreCase: true);
        Assert.Single(expression.Arguments);
    }

    [Fact]
    public void ParseQuery_TypedDateLiteral_RemainsLiteral()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT DATE '2026-08-23'", SqlAgentToolType.Postgres);
        Assert.IsType<LiteralExpr>(Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Select).Expression);
    }

    [Fact]
    public void Compile_MySqlDateFunction_PreservesNativeSemantics()
    {
        var command = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT DATE(created_at) FROM events",
                SqlAgentToolType.MySQL),
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-date-function-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlDateFunction_CrossProviderRemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT DATE(created_at) FROM events",
                    SqlAgentToolType.MySQL),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("mysql-date-function-cross-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("temporal.date_only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE(expr)", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cross-dialect lowering", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void ParseQuery_CommaLimit_NormalizesOffsetAndRowCount(SqlAgentToolType sourceDialect)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users LIMIT 5, 10",
            sourceDialect);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(10, select.Limit);
        Assert.Equal(5, select.Offset);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_CommaLimit_IsRejectedForPostgresRawSource()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users LIMIT 5, 10",
                SqlAgentToolType.Postgres));

        Assert.Contains("offset,row_count", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL and SQLite", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void ParseQuery_CommaLimit_CannotAlsoUseSeparateOffset(SqlAgentToolType sourceDialect)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users LIMIT 5, 10 OFFSET 2",
                sourceDialect));

        Assert.Contains("cannot be combined", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void ParseDml_InsertSelectCommaLimit_UsesSameSourceDialect(SqlAgentToolType sourceDialect)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO archived_users (id) SELECT id FROM users LIMIT 5, 10",
            sourceDialect);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var source = Assert.IsType<InsertQuerySource>(insert.Source);
        var select = Assert.IsType<SelectStatement>(source.Query);

        Assert.Equal(10, select.Limit);
        Assert.Equal(5, select.Offset);
    }

    [Fact]
    public void ParseQuery_ExtractYear_UsesPortableDatePartFamily()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT EXTRACT(YEAR FROM created_at) FROM events",
            SqlAgentToolType.Postgres);
        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("EXTRACT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("YEAR", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_UnknownExtractUnit_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT EXTRACT(FOOBAR FROM created_at) FROM events",
                SqlAgentToolType.Postgres));
        Assert.Contains("FOOBAR", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_CteColumnAliases_AreModeledInsteadOfDiscarded()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH recent(id) AS (SELECT id FROM orders) SELECT id FROM recent",
            SqlAgentToolType.Postgres);
        var cte = Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Ctes);
        Assert.Equal("recent", Assert.Single(cte.Name.Parts).Value);
        Assert.Equal("id", Assert.Single(Assert.Single(cte.ColumnAliases).Parts).Value);
        Assert.NotEqual(SourceSpan.Unknown, cte.Span);
    }

    [Fact]
    public void ParseQuery_RecursiveCte_PreservesRecursiveScope()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH RECURSIVE x AS (SELECT 1) SELECT * FROM x",
            SqlAgentToolType.Postgres);
        var cte = Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Ctes);

        Assert.True(cte.RecursiveScope);
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
        var predicate = Assert.IsType<BinaryExpr>(Assert.IsType<DeleteStatement>(parsed.Statement).Predicate);
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
        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(parsed);
        Assert.Contains("audit.events", facts.ReferencedTables);
        Assert.Contains("crm.users", facts.ReferencedTables);
        Assert.DoesNotContain("x", facts.ReferencedTables);
    }
}
