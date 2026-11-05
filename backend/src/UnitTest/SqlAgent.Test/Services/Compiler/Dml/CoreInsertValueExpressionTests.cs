using Xunit;

namespace SqlAgent.Test.Services;

public class CoreInsertValueExpressionTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileInsert_ArithmeticValue_IsStructuredAndParameterized(SqlAgentToolType provider)
    {
        var command = Compile(
            "INSERT INTO metrics(quantity) VALUES (1 + 2)",
            provider,
            provider);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("INSERT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+", command.Sql, StringComparison.Ordinal);
        Assert.Equal(
            new object?[] { 1, 2 },
            command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileInsert_CurrentTimestamp_IsProviderExpression(SqlAgentToolType provider)
    {
        var command = Compile(
            "INSERT INTO events(created_at) VALUES (CURRENT_TIMESTAMP)",
            provider,
            provider);

        Assert.Contains("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void CompileInsert_CrossDialectCast_UsesTargetTypeAndBinding()
    {
        var command = Compile(
            "INSERT INTO metrics(quantity) VALUES (CAST('12' AS INTEGER))",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains("CAST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SIGNED", command.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal("12", parameter.Value);
    }

    [Fact]
    public void CompileInsert_CaseValue_PreservesBranchesAndParameters()
    {
        var command = Compile(
            "INSERT INTO metrics(label) VALUES (CASE WHEN 1 = 1 THEN 'yes' ELSE 'no' END)",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "yes"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "no"));
    }

    [Fact]
    public void CompileInsert_ScalarSubquery_IsAllowedAndAuthorized()
    {
        var command = Compile(
            "INSERT INTO archive(max_id) VALUES ((SELECT MAX(id) FROM users))",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "archive", "users" });

        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileInsert_OuterColumnReference_IsRejectedAfterNormalization()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO metrics(quantity) VALUES (quantity + 1)",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("cannot reference column", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT ... SELECT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileInsert_AggregateValue_IsRejectedInAssignmentSemanticContext()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO metrics(quantity) VALUES (SUM(1))",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("Aggregate function", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE SET", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void CompileInsert_DefiniteBooleanValue_FailsAtCapabilityBoundary(SqlAgentToolType provider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO metrics(flag) VALUES (1 = 1)",
            provider,
            provider));

        Assert.Contains("dml.insert.boolean_value", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "VALUES")]
    [InlineData(SqlAgentToolType.MySQL, "VALUES")]
    [InlineData(SqlAgentToolType.MsSqlServer, "VALUES")]
    [InlineData(SqlAgentToolType.Sqlite, "VALUES")]
    [InlineData(SqlAgentToolType.Oracle, "INSERT ALL")]
    [InlineData(SqlAgentToolType.Firebird, "UNION ALL")]
    public void CompileInsert_MultiRowExpressions_UseProviderNativeShape(
        SqlAgentToolType provider,
        string expectedSql)
    {
        var command = Compile(
            "INSERT INTO metrics(quantity) VALUES (1 + 2), (3 + 4)",
            provider,
            provider);

        Assert.Contains(expectedSql, command.Sql, StringComparison.OrdinalIgnoreCase);
        if (provider == SqlAgentToolType.Oracle)
            Assert.Contains("SELECT 1 FROM DUAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        if (provider == SqlAgentToolType.Firebird)
            Assert.Contains("RDB$DATABASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new object?[] { 1, 2, 3, 4 },
            command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        IReadOnlySet<string>? allowedTables = null) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1", allowedTables));
}
