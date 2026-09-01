using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSemanticDriftGuardTests
{
    [Fact]
    public void Compile_SimpleCase_WithRepeatableColumnOperand_RemainsSupported()
    {
        var command = Compile(
            "SELECT CASE status WHEN 'ready' THEN 1 ELSE 0 END FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SimpleCase_WithFunctionOperand_PreservesSingleEvaluationShape()
    {
        var command = Compile(
            "SELECT CASE RANDOM() WHEN 0 THEN 1 WHEN 1 THEN 2 ELSE 0 END FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("CASE RANDOM()", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(command.Sql, "RANDOM()", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compile_MySqlPipesOperator_FailsBecauseSqlModeIsNotModeled()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT first_name || last_name FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("PIPES_AS_CONCAT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresConcat_ToMySql_RemainsSupported()
    {
        var command = Compile(
            "SELECT first_name || last_name FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains("CONCAT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_IlikeFromNonPostgresSource_FailsAtSourceSemanticBoundary(
        SqlAgentToolType sourceDialect)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT name FROM users WHERE name ILIKE 'a%'",
            sourceDialect,
            SqlAgentToolType.Postgres));

        Assert.Contains("PostgreSQL-specific", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceDialect.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RandomAcrossDialects_FailsInsteadOfRenamingFunction()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT RANDOM() FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL));

        Assert.Contains("not translated across dialects", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RandomWithinSameDialect_RemainsSupported()
    {
        var command = Compile(
            "SELECT RANDOM() FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("RANDOM()", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerLen_ToPostgres_PreservesTrailingSpaceRule()
    {
        var command = Compile(
            "SELECT LEN(name) FROM users",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Contains("LENGTH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RTRIM", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PortableLength_ToSqlServer_FailsInsteadOfUsingLen()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT LENGTH(name) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("trailing spaces", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StandardCoalesce_RemainsStandardAcrossDialects()
    {
        var command = Compile(
            "SELECT COALESCE(nickname, name) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.Contains("COALESCE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NVL(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlIfNullAcrossDialects_FailsOnTypeSemantics()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT IFNULL(nickname, name) FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("type-conversion rules", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlGroupConcat_DefaultSeparator_UsesNativeAggregateSyntax()
    {
        var command = Compile(
            "SELECT GROUP_CONCAT(name) FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL);

        Assert.Contains("GROUP_CONCAT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR ','", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(command.Parameters, parameter => Equals(parameter.Value, ","));
    }

    [Fact]
    public void Compile_MySqlGroupConcat_SecondExpression_IsNotMisreadAsSeparator()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT GROUP_CONCAT(first_name, last_name) FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("multiple value expressions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_StringAggDefaultSeparator_ToMySql_UsesGroupConcat()
    {
        var command = Compile(
            "SELECT STRING_AGG(name, ',') FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains("GROUP_CONCAT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR ','", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(command.Parameters, parameter => Equals(parameter.Value, ","));
    }

    [Fact]
    public void Compile_StringAggCustomSeparator_ToMySql_UsesSeparatorClause()
    {
        var command = Compile(
            "SELECT STRING_AGG(name, '|') FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains("GROUP_CONCAT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SEPARATOR '|'", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CORE_STRING_AGG", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_GroupConcatInWhere_IsRejectedAsAggregatePlacement()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT name FROM users WHERE GROUP_CONCAT(name) = 'x'",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL));

        Assert.Contains("Aggregate function 'CORE_STRING_AGG'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlDoubleQuote_FailsBecauseAnsiQuotesModeIsUnknown()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery("SELECT \"name\" FROM users", SqlAgentToolType.MySQL));

        Assert.Contains("ANSI_QUOTES", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_PostgresBacktickIdentifier_FailsAtDialectBoundary()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery("SELECT `name` FROM users", SqlAgentToolType.Postgres));

        Assert.Contains("Backtick-quoted identifiers", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_PostgresBracketIdentifier_FailsAtDialectBoundary()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery("SELECT [name] FROM users", SqlAgentToolType.Postgres));

        Assert.Contains("Bracket-quoted identifiers", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlBacktickIdentifier_RemainsSupported()
    {
        var command = Compile(
            "SELECT `name` FROM `users`",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL);

        Assert.Contains("name", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerBracketIdentifier_RemainsSupported()
    {
        var command = Compile(
            "SELECT [name] FROM [users]",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("name", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_OracleBareSysdate_IsStructuredInsteadOfBecomingColumnReference()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT SYSDATE FROM dual",
            SqlAgentToolType.Oracle);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.IsType<FunctionCallExpr>(Assert.Single(select.Select).Expression);
    }

    [Fact]
    public void Parse_OracleSysdateCall_RemainsRejectedBecauseOracleSyntaxIsBare()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT SYSDATE() FROM dual",
                SqlAgentToolType.Oracle));

        Assert.Contains("SYSDATE", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parentheses", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_NonOracleSysdateCall_RemainsRejectedAtSourceCapabilityBoundary(
        SqlAgentToolType provider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SYSDATE() FROM users",
            provider,
            provider));

        Assert.Contains("SYSDATE", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not valid for declared source dialect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string value, string token, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, comparison)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, sourceDialect);
        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
    }
}
