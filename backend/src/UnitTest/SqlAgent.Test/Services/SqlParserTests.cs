using SqlAgent.Service.Validation;
using Xunit;

namespace SqlAgent.Test.Services;

public class SqlParserTests
{
    [Fact]
    public void Parse_OrderByWithLimit_PreservesRequestedLimit()
    {
        const string sql = """
            SELECT order_id, customer_id, employee_id, order_date, required_date,
                   shipped_date, ship_via, freight, ship_name, ship_city, ship_country
            FROM orders
            ORDER BY order_date DESC, order_id DESC
            LIMIT 5
            """;

        var definition = SqlDefinitionParser.ParseQuery(sql);

        Assert.Equal(5, definition.Limit);
        Assert.NotNull(definition.OrderByColumns);
        Assert.Equal(2, definition.OrderByColumns.Count);
        Assert.All(definition.OrderByColumns, order => Assert.Equal(SortDirection.Desc, order.Direction));
    }

    [Fact]
    public void Parse_PostgresDateCastInSelectAndHaving_PreservesCastAst()
    {
        const string sql = """
            WITH SystemMax AS (
                SELECT MAX(order_date) AS max_system_date FROM orders
            )
            SELECT
                c.customer_id,
                c.company_name,
                c.contact_name,
                c.phone,
                sm.max_system_date,
                MAX(o.order_date) AS last_order_date,
                (sm.max_system_date::date - MAX(o.order_date)::date) AS days_since_last_order
            FROM customers c
            LEFT JOIN orders o ON c.customer_id = o.customer_id
            CROSS JOIN SystemMax sm
            GROUP BY
                c.customer_id,
                c.company_name,
                c.contact_name,
                c.phone,
                sm.max_system_date
            HAVING
                (sm.max_system_date::date - MAX(o.order_date)::date) > 180
                OR MAX(o.order_date) IS NULL
            ORDER BY days_since_last_order DESC;
            """;

        var definition = SqlDefinitionParser.ParseQuery(sql);
        var errors = DefinitionValidator.Validate(definition);

        Assert.Empty(errors);
        var dateDiff = Assert.IsType<OperationSelectCondition>(definition.SelectColumns![6]);
        Assert.Equal(ArithmeticOperator.Subtract, dateDiff.Operator);
        Assert.Equal("DATE", Assert.IsType<CastSelectCondition>(dateDiff.Left).TypeName, ignoreCase: true);
        Assert.Equal("DATE", Assert.IsType<CastSelectCondition>(dateDiff.Right).TypeName, ignoreCase: true);
        var havingGroup = Assert.IsType<GroupHavingCondition>(Assert.Single(definition.HavingConditions!));
        var having = Assert.IsType<ExpressionHavingCondition>(havingGroup.Groups[0]);
        var havingDifference = Assert.IsType<OperationSelectCondition>(having.LeftExpression);
        Assert.IsType<CastSelectCondition>(havingDifference.Left);
        Assert.IsType<CastSelectCondition>(havingDifference.Right);
    }

    [Theory]
    [InlineData("SELECT price % 10 FROM products", ArithmeticOperator.Modulo)]
    [InlineData("SELECT price >= 10 FROM products", ArithmeticOperator.GreaterThanOrEqual)]
    [InlineData("SELECT active = TRUE AND deleted = FALSE FROM products", ArithmeticOperator.And)]
    [InlineData("SELECT active = TRUE OR deleted = FALSE FROM products", ArithmeticOperator.Or)]
    public void Parse_SelectExpression_PreservesOperator(string sql, ArithmeticOperator expected)
    {
        var definition = SqlDefinitionParser.ParseQuery(sql);

        var operation = Assert.IsType<OperationSelectCondition>(Assert.Single(definition.SelectColumns!));
        Assert.Equal(expected, operation.Operator);
    }

    [Fact]
    public void Parse_NullsFirst_PreservesOrdering()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT value FROM t ORDER BY value NULLS FIRST");

        Assert.Equal(NullOrdering.First, Assert.Single(definition.OrderByColumns!).NullOrdering);
    }

    [Fact]
    public void Parse_WindowFrame_PreservesUnitAndBounds()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT SUM(value) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) FROM t");

        var function = Assert.IsType<FunctionSelectCondition>(Assert.Single(definition.SelectColumns!));
        var frame = Assert.IsType<WindowFrameDefinition>(function.Window!.Frame);
        Assert.Equal(WindowFrameUnit.Rows, frame.Unit);
        Assert.Equal(WindowFrameBoundKind.Preceding, frame.Start.Kind);
        Assert.Equal(2, frame.Start.Offset);
        Assert.Equal(WindowFrameBoundKind.CurrentRow, frame.End!.Kind);
    }

    [Fact]
    public void Parse_Interval_PreservesLiteral()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT created_at + INTERVAL '1 day' FROM t");

        var operation = Assert.IsType<OperationSelectCondition>(Assert.Single(definition.SelectColumns!));
        Assert.Equal("1 day", Assert.IsType<IntervalSelectCondition>(operation.Right).Literal);
    }

    [Theory]
    [InlineData("CURRENT_DATE")]
    [InlineData("CURRENT_TIME")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("CURRENT_TIMESTAMP()")]
    public void Parse_CurrentTemporalKeyword_DoesNotTreatItAsAColumn(string expression)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {expression} AS value FROM t");

        Assert.IsNotType<FieldSelectCondition>(Assert.Single(definition.SelectColumns!));
    }

    [Fact]
    public void Parse_Cast_PreservesTypeAndPrecision()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT CAST(amount AS DECIMAL(12,2)) FROM t");

        var cast = Assert.IsType<CastSelectCondition>(Assert.Single(definition.SelectColumns!));
        Assert.Equal("DECIMAL(12,2)", cast.TypeName, ignoreCase: true);
    }

    [Theory]
    [InlineData("SELECT ? FROM t", "?")]
    [InlineData("SELECT :name FROM t", ":name")]
    [InlineData("SELECT @name FROM t", "@name")]
    [InlineData("SELECT $1 FROM t", "$1")]
    [InlineData("SELECT {{name}} FROM t", "{{name}}")]
    public void Parse_UnboundParameter_RejectsExplicitly(string sql, string parameter)
    {
        var error = Assert.Throws<SqlParseException>(() => SqlDefinitionParser.ParseQuery(sql));

        Assert.Contains(parameter, error.Message);
        Assert.Contains("Unbound SQL parameter", error.Message);
    }

    [Theory]
    [InlineData("SELECT 'unterminated FROM t", "Unterminated string literal")]
    [InlineData("SELECT \"unterminated FROM t", "Unterminated quoted identifier")]
    [InlineData("SELECT * FROM t /* unterminated", "Unterminated block comment")]
    [InlineData("SELECT {{bad FROM t", "Unterminated template parameter")]
    [InlineData("SELECT # FROM t", "Unexpected character")]
    [InlineData("SELECT 1.2.3 FROM t", "Invalid numeric literal")]
    public void Tokenize_MalformedInput_FailsClosedWithSpan(string sql, string expectedMessage)
    {
        var error = Assert.Throws<SqlParseException>(() => new SqlTokenizer(sql).Tokenize());

        Assert.Contains(expectedMessage, error.Message);
        Assert.Contains("span [", error.Message);
    }

    [Fact]
    public void Parse_TrailingUnconsumedTokens_Rejects()
    {
        var error = Assert.Throws<SqlParseException>(() => SqlDefinitionParser.ParseQuery("SELECT id FROM users garbage extra"));

        Assert.Contains("complete statement was not consumed", error.Message);
    }

    [Fact]
    public void Parse_StringLiteral_PreservesEscapedQuoteValue()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT 'O''Brien' AS name");

        var constant = Assert.IsType<ConstantSelectCondition>(Assert.Single(definition.SelectColumns!));
        Assert.Equal("O'Brien", constant.Constant);
    }

    [Theory]
    [InlineData("UPDATE users SET name = 'unterminated WHERE id = 1", "Unterminated string literal")]
    [InlineData("UPDATE users SET name = :name WHERE id = 1", "Unbound SQL parameter")]
    [InlineData("UPDATE users SET login_count = login_count + 1 WHERE id = 1", "Unsupported DML value expression")]
    [InlineData("DELETE FROM users; DELETE FROM audit", "Only one SQL statement")]
    public void ParseDml_MalformedOrUnboundInput_FailsClosed(string sql, string expectedMessage)
    {
        var error = Assert.Throws<SqlParseException>(() => SqlDefinitionParser.ParseDml(sql));

        Assert.Contains(expectedMessage, error.Message);
    }

    [Fact]
    public void ParseDml_MultiRowInsert_PreservesRowsAndNulls()
    {
        var definition = SqlDefinitionParser.ParseDml(
            "INSERT INTO users (id, name, score) VALUES (1, 'A', NULL), (2, 'B', 9.5)");

        Assert.Equal(DmlOperation.Insert, definition.Operation);
        Assert.Equal(["id", "name", "score"], definition.Columns);
        Assert.Equal(2, definition.MultiValues!.Count);
        Assert.Null(definition.MultiValues[0][2]);
        Assert.Equal(9.5m, definition.MultiValues[1][2]);
    }

    [Fact]
    public void ParseDml_DateLiteral_ProducesTypedDateOnlyValue()
    {
        var definition = SqlDefinitionParser.ParseDml(
            "UPDATE orders SET order_date = DATE '2026-08-21', required_date = DATE '2026-08-21' WHERE id = 1",
            SqlAgentToolType.Postgres);

        Assert.All(definition.Values!, value =>
            Assert.Equal(
                new DateOnly(2026, 8, 21),
                Assert.IsType<SqlDateValue>(value.Value).Value));
    }

    [Theory]
    [InlineData("DATE '2026-02-30'", "Invalid DATE literal")]
    [InlineData("DATE 20260821", "DATE must be followed")]
    public void ParseDml_InvalidDateLiteral_FailsClosed(string expression, string expectedMessage)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            SqlDefinitionParser.ParseDml($"UPDATE orders SET order_date = {expression} WHERE id = 1"));

        Assert.Contains(expectedMessage, error.Message);
    }

    [Fact]
    public void ParseQuery_DateLiteral_UsesSameCanonicalValueAsDml()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT DATE '2026-08-21' AS report_date");

        var constant = Assert.IsType<ConstantSelectCondition>(Assert.Single(definition.SelectColumns!));
        Assert.Equal(new DateOnly(2026, 8, 21), Assert.IsType<SqlDateValue>(constant.Constant).Value);
    }

    [Fact]
    public void ParseQuery_InvalidDateLiteral_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            SqlDefinitionParser.ParseQuery("SELECT DATE '2026-02-30'"));

        Assert.Contains("Invalid DATE literal", error.Message);
    }

    [Theory]
    [InlineData("TIME '09:30'", 9, 30, 0)]
    [InlineData("TIME '23:59:58.1234567'", 23, 59, 58)]
    public void ParseDml_TimeLiteral_UsesCanonicalTimeValue(
        string expression,
        int hour,
        int minute,
        int second)
    {
        var definition = SqlDefinitionParser.ParseDml(
            $"UPDATE schedules SET starts_at = {expression} WHERE id = 1");

        var time = Assert.IsType<SqlTimeValue>(Assert.Single(definition.Values!).Value).Value;
        Assert.Equal(hour, time.Hour);
        Assert.Equal(minute, time.Minute);
        Assert.Equal(second, time.Second);
    }

    [Theory]
    [InlineData("TIMESTAMP '2026-08-21 09:30:15.1234567'", typeof(SqlLocalDateTimeValue))]
    [InlineData("TIMESTAMP '2026-08-21T09:30:15+08:00'", typeof(SqlOffsetDateTimeValue))]
    [InlineData("TIMESTAMP '2026-08-21T01:30:15Z'", typeof(SqlOffsetDateTimeValue))]
    public void ParseDml_TimestampLiteral_PreservesTimezoneIntent(string expression, Type expectedType)
    {
        var definition = SqlDefinitionParser.ParseDml(
            $"UPDATE events SET occurred_at = {expression} WHERE id = 1");

        Assert.IsType(expectedType, Assert.Single(definition.Values!).Value);
    }

    [Fact]
    public void ParseQuery_TemporalLiterals_UseCanonicalValues()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT TIME '09:30:15', TIMESTAMP '2026-08-21 09:30:15', " +
            "TIMESTAMP '2026-08-21T09:30:15+08:00'");

        Assert.IsType<SqlTimeValue>(Assert.IsType<ConstantSelectCondition>(definition.SelectColumns![0]).Constant);
        var local = Assert.IsType<SqlLocalDateTimeValue>(
            Assert.IsType<ConstantSelectCondition>(definition.SelectColumns[1]).Constant);
        Assert.Equal(DateTimeKind.Unspecified, local.Value.Kind);
        var offset = Assert.IsType<SqlOffsetDateTimeValue>(
            Assert.IsType<ConstantSelectCondition>(definition.SelectColumns[2]).Constant);
        Assert.Equal(TimeSpan.FromHours(8), offset.Value.Offset);
    }

    [Theory]
    [InlineData("TIME '24:00:00'")]
    [InlineData("TIME '09:30:00+08:00'")]
    [InlineData("TIMESTAMP '08/21/2026 09:30:00'")]
    public void ParseDml_NonPortableTemporalLiteral_FailsClosed(string expression)
    {
        Assert.Throws<SqlParseException>(() =>
            SqlDefinitionParser.ParseDml($"UPDATE events SET occurred_at = {expression} WHERE id = 1"));
    }

    [Theory]
    [InlineData("TIMESTAMP WITH TIME ZONE '2026-08-21T09:30:00+08:00'", typeof(SqlOffsetDateTimeValue))]
    [InlineData("TIMESTAMP WITHOUT TIME ZONE '2026-08-21 09:30:00'", typeof(SqlLocalDateTimeValue))]
    public void ParseDml_ExplicitTimestampTimezoneIntent_IsPreserved(string expression, Type expectedType)
    {
        var definition = SqlDefinitionParser.ParseDml(
            $"UPDATE events SET occurred_at = {expression} WHERE id = 1");

        Assert.IsType(expectedType, Assert.Single(definition.Values!).Value);
    }

    [Theory]
    [InlineData("TIMESTAMP WITH TIME ZONE '2026-08-21 09:30:00'")]
    [InlineData("TIMESTAMP WITHOUT TIME ZONE '2026-08-21T09:30:00+08:00'")]
    [InlineData("TIME WITH TIME ZONE '09:30:00+08:00'")]
    public void ParseDml_ContradictoryOrUnsupportedTimezoneIntent_FailsClosed(string expression)
    {
        Assert.Throws<SqlParseException>(() =>
            SqlDefinitionParser.ParseDml($"UPDATE events SET occurred_at = {expression} WHERE id = 1"));
    }

    [Fact]
    public void ParseDml_ComplexPredicate_UsesStructuredWhereParser()
    {
        var definition = SqlDefinitionParser.ParseDml(
            "DELETE FROM users WHERE (status = 'old' OR status IS NULL) AND id IN (1, 2) AND age BETWEEN 18 AND 65");

        var root = Assert.IsType<GroupWhereCondition>(Assert.Single(definition.WhereConditions!));
        Assert.Contains(root.Groups, condition => condition is GroupWhereCondition { IsOr: true });
        Assert.Contains(root.Groups, condition => condition is BasicWhereCondition { Operator: "IN" });
        Assert.Contains(root.Groups, condition => condition is BasicWhereCondition { Operator: "BETWEEN" });
    }

    [Fact]
    public void ParseDml_UpdateStringContainingWhere_DoesNotSplitInsideLiteral()
    {
        var definition = SqlDefinitionParser.ParseDml(
            "UPDATE main.notes SET body = 'look where the value is', title = 'set, where' WHERE id = 7");

        Assert.Equal("main.notes", definition.TableName);
        Assert.Equal("look where the value is", definition.Values![0].Value);
        Assert.Equal("set, where", definition.Values[1].Value);
        var where = Assert.IsType<BasicWhereCondition>(Assert.Single(definition.WhereConditions!));
        Assert.Equal("id", where.FieldName);
        Assert.Equal(7, where.Value);
    }

    [Fact]
    public void ParseDml_QuotedIdentifiers_AreNormalizedByTokenizer()
    {
        var definition = SqlDefinitionParser.ParseDml(
            "UPDATE \"app\".\"Order\" SET \"Display Name\" = 'ready' WHERE \"Id\" = 1",
            SqlAgentToolType.Postgres);

        Assert.Equal("app.Order", definition.TableName);
        Assert.Equal("Display Name", Assert.Single(definition.Values!).FieldName);
        Assert.Equal("Id", Assert.IsType<BasicWhereCondition>(Assert.Single(definition.WhereConditions!)).FieldName);
    }

    [Fact]
    public void Parse_QuotedIdentifiers_NormalizesForTargetCompiler()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT \"Order\".\"Value\" FROM \"Order\"",
            SqlAgentToolType.Postgres);

        Assert.Equal("Order", definition.TableName);
        Assert.Equal("Order.Value", Assert.IsType<FieldSelectCondition>(Assert.Single(definition.SelectColumns!)).FieldName);
    }

    [Theory]
    [InlineData("SELECT $$O'Brien$$ AS value", "O'Brien")]
    [InlineData("SELECT $tag$line 1\nline '2'$tag$ AS value", "line 1\nline '2'")]
    [InlineData("SELECT E'line\\n2' AS value", "line\n2")]
    public void Parse_PostgresStringForms_PreserveDecodedValue(string sql, string expected)
    {
        var definition = SqlDefinitionParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var constant = Assert.IsType<ConstantSelectCondition>(Assert.Single(definition.SelectColumns!));
        Assert.Equal(expected, constant.Constant);
    }

    [Fact]
    public void Parse_OracleQString_PreservesDecodedValue()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT q'[O'Brien]' AS value",
            SqlAgentToolType.Oracle);

        var constant = Assert.IsType<ConstantSelectCondition>(Assert.Single(definition.SelectColumns!));
        Assert.Equal("O'Brien", constant.Constant);
    }

    [Fact]
    public void Tokenize_MySqlHashComment_IsProviderSpecific()
    {
        var mysql = new SqlTokenizer("SELECT 1 # comment", SqlAgentToolType.MySQL).Tokenize();
        Assert.DoesNotContain(mysql, token => token.Value == "#");

        Assert.Throws<SqlParseException>(() =>
            new SqlTokenizer("SELECT 1 # comment", SqlAgentToolType.Postgres).Tokenize());
    }

    [Fact]
    public void Parse_SqlServerTempTable_DoesNotTreatHashAsComment()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT id FROM #agent_results",
            SqlAgentToolType.MsSqlServer);

        Assert.Equal("#agent_results", definition.TableName);
    }

    [Fact]
    public void Tokenize_UnterminatedDollarQuote_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            new SqlTokenizer("SELECT $tag$unterminated", SqlAgentToolType.Postgres).Tokenize());

        Assert.Contains("Unterminated PostgreSQL dollar-quoted string", error.Message);
        Assert.Contains("span [", error.Message);
    }
}
