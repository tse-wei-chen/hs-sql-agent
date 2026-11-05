using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCastGrammarExpansionTests
{
    [Theory]
    [InlineData("VARCHAR(MAX)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("VARBINARY(MAX)")]
    public void Compile_SqlServerNativeMaxTypes_PreserveLargeValueSyntax(string typeName)
    {
        var command = CompileQuery(
            $"SELECT CAST('abc' AS {typeName})",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains(typeName, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerNvarcharMaxToPostgres_UsesUnboundedText()
    {
        var command = CompileQuery(
            "SELECT CAST('abc' AS NVARCHAR(MAX))",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Contains("CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AS TEXT)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MAX", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresTimestampWithPrecisionAndZoneToSqlServer_UsesDateTimeOffsetPrecision()
    {
        var command = CompileQuery(
            "SELECT CAST(CURRENT_TIMESTAMP AS TIMESTAMP(6) WITH TIME ZONE)",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("DATETIMEOFFSET(6)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleTimestampZonePrecisionAbovePostgresCapacity_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT CAST(CURRENT_TIMESTAMP AS TIMESTAMP(9) WITH TIME ZONE)",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Postgres));

        Assert.Contains("precision 9", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Postgres", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresTimestampPrecisionToFirebird_ValidatesButOmitsTypePrecision()
    {
        var command = CompileQuery(
            "SELECT CAST(CURRENT_TIMESTAMP AS TIMESTAMP(4))",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Firebird);

        Assert.Contains("AS TIMESTAMP)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TIMESTAMP(4)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresTimestampPrecisionAboveFirebirdCapacity_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT CAST(CURRENT_TIMESTAMP AS TIMESTAMP(5))",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Firebird));

        Assert.Contains("precision 5", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Firebird", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresDoubleColonCast_RemainsValidRawSourceSyntax()
    {
        var command = CompileQuery(
            "SELECT 'abc'::VARCHAR(20)",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VARCHAR(20)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("::", command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_DoubleColonCast_FailsClosedForNonPostgresRawSource(SqlAgentToolType sourceDialect)
    {
        var error = Assert.Throws<SqlParseException>(() => CompileQuery(
            "SELECT 'abc'::VARCHAR(20)",
            sourceDialect,
            SqlAgentToolType.Postgres));

        Assert.Contains("::", error.Message, StringComparison.Ordinal);
        Assert.Contains(sourceDialect.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlStandardCast_RemainsPortable()
    {
        var command = CompileQuery(
            "SELECT CAST(1 AS SIGNED)",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres);

        Assert.Contains("CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BIGINT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DmlDoubleColonCast_UsesTheSameRawSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "UPDATE users SET status = status::VARCHAR(20)",
                SqlAgentToolType.MySQL));

        Assert.Contains("::", error.Message, StringComparison.Ordinal);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DmlPredicateSupportsSqlServerMaxCastGrammar()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "UPDATE users SET status = 'x' WHERE CAST(name AS VARCHAR(MAX)) = 'a'",
                SqlAgentToolType.MsSqlServer),
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Contains("VARCHAR(MAX)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source),
            target,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
