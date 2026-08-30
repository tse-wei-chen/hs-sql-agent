using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlAssignmentExpressionTests
{
    [Fact]
    public void ParseUpdate_CurrentDate_IsModeledAsCurrentTemporalFunction()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE orders SET order_date = CURRENT_DATE, required_date = CURRENT_DATE WHERE order_id = 11077",
            SqlAgentToolType.Postgres);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);

        Assert.Equal(2, update.Assignments.Length);
        Assert.All(update.Assignments, assignment =>
        {
            var function = Assert.IsType<FunctionCallExpr>(assignment.Value);
            Assert.Equal("CURRENT_DATE", Assert.Single(function.Name.Parts).Value, ignoreCase: true);
            Assert.Empty(function.Arguments);
            Assert.False(function.IsDistinct);
        });
    }

    [Fact]
    public void CompileUpdate_CurrentDate_UsesClosedSqlExpressionAndKeepsPredicateParameterized()
    {
        var command = Compile(
            "UPDATE orders SET order_date = CURRENT_DATE, required_date = CURRENT_DATE WHERE order_id = 11077",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("CURRENT_DATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal(11077L, Convert.ToInt64(parameter.Value));
    }

    [Fact]
    public void CompileUpdate_CurrentDate_UsesSqlServerDateSemanticsWhenTargetChanges()
    {
        var command = Compile(
            "UPDATE orders SET order_date = CURRENT_DATE WHERE order_id = 11077",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("CAST(CURRENT_TIMESTAMP AS date)", command.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal(11077, parameter.Value);
    }

    [Fact]
    public void ParseAndCompileUpdate_PostgresDateCast_BecomesTypedParameterizedLiteral()
    {
        const string sql = "UPDATE orders SET order_date = '2026-08-23'::date, required_date = '2026-08-23'::date WHERE order_id = 11077";
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);

        Assert.All(update.Assignments, assignment =>
        {
            var literal = Assert.IsType<LiteralExpr>(assignment.Value);
            var date = Assert.IsType<SqlDateValue>(literal.Value);
            Assert.Equal(new DateOnly(2026, 8, 23), date.Value);
        });

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.DoesNotContain("2026-08-23", command.Sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Count(parameter =>
            parameter.Value is DateTime dateTime && dateTime == new DateTime(2026, 8, 23)));
        Assert.Contains(command.Parameters, parameter =>
            parameter.Value is long value && value == 11077L);
    }

    [Fact]
    public void CompileUpdate_MultipleLiteralAssignments_PreservesBindingOrder()
    {
        var command = Compile(
            "UPDATE orders SET status = 'open', quantity = 2 WHERE order_id = 11077",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.DoesNotContain("open", command.Sql, StringComparison.Ordinal);
        Assert.Equal(3, command.Parameters.Length);
        Assert.Equal("open", command.Parameters[0].Value);
        Assert.Equal(2L, Convert.ToInt64(command.Parameters[1].Value));
        Assert.Equal(11077L, Convert.ToInt64(command.Parameters[2].Value));
    }

    [Fact]
    public void ParseUpdate_ArithmeticAssignment_IsStructuredExpression()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE orders SET quantity = quantity + 1 WHERE order_id = 11077",
            SqlAgentToolType.Postgres);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        var assignment = Assert.Single(update.Assignments);
        var binary = Assert.IsType<BinaryExpr>(assignment.Value);

        Assert.Equal("+", binary.Operator);
        Assert.IsType<ColumnExpr>(binary.Left);
        Assert.Equal(1L, Convert.ToInt64(Assert.IsType<LiteralExpr>(binary.Right).Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileUpdate_ArithmeticAssignment_CompilesAcrossProviders(SqlAgentToolType targetProvider)
    {
        var command = Compile(
            "UPDATE orders SET quantity = quantity + 1 WHERE order_id = 11077",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains(" SET ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+", command.Sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(1L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal(11077L, Convert.ToInt64(command.Parameters[1].Value));
    }

    [Fact]
    public void CompileUpdate_ScalarFunctionAssignment_UsesCanonicalExpressionLowering()
    {
        var command = Compile(
            "UPDATE users SET name = LOWER(name) WHERE id = 7",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.Contains("LOWER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7L, Convert.ToInt64(Assert.Single(command.Parameters).Value));
    }

    [Fact]
    public void CompileUpdate_CaseAssignment_KeepsBranchValuesParameterized()
    {
        var command = Compile(
            "UPDATE orders SET status = CASE WHEN amount > 100 THEN 'large' ELSE 'small' END WHERE order_id = 7",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("large", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("small", command.Sql, StringComparison.Ordinal);
        Assert.Equal(4, command.Parameters.Length);
        Assert.Equal(100L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal("large", command.Parameters[1].Value);
        Assert.Equal("small", command.Parameters[2].Value);
        Assert.Equal(7L, Convert.ToInt64(command.Parameters[3].Value));
    }

    [Fact]
    public void CompileUpdate_NonDateCastAssignment_RemainsStructured()
    {
        var command = Compile(
            "UPDATE users SET score = CAST(score AS DECIMAL(12,2)) WHERE id = 7",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("CAST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DECIMAL(12,2)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, Assert.Single(command.Parameters).Value);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void CompileUpdate_DefiniteBooleanAssignment_FailsAtCapabilityBoundary(SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "UPDATE orders SET flagged = amount > 100 WHERE order_id = 7",
            SqlAgentToolType.Postgres,
            targetProvider));

        Assert.Contains("dml.update.boolean_assignment", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileUpdate_AggregateAssignment_FailsBeforeLowering()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "UPDATE orders SET amount = SUM(amount) WHERE order_id = 7",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("Aggregate function", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE SET", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseInsert_CurrentDate_IsModeledAsScalarExpression()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO orders(order_date) VALUES (CURRENT_DATE)",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var values = Assert.IsType<InsertValuesSource>(insert.Source);
        var row = Assert.Single(values.Rows);
        var function = Assert.IsType<FunctionCallExpr>(Assert.Single(row));

        Assert.Equal("CURRENT_DATE", Assert.Single(function.Name.Parts).Value, ignoreCase: true);
        Assert.Empty(function.Arguments);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"));
}
