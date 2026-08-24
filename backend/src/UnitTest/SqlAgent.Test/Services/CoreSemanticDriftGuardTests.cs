using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
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
    public void Parse_SimpleCase_WithFunctionOperand_FailsInsteadOfRepeatingEvaluation()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT CASE RANDOM() WHEN 0 THEN 1 ELSE 0 END FROM orders",
                SqlAgentToolType.Postgres));

        Assert.Contains("Simple CASE operands", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repeated equality", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Compile_IlikeFromNonPostgresSource_FailsAtSourceSemanticBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT name FROM users WHERE name ILIKE 'a%'",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("PostgreSQL-specific", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void Compile_MySqlGroupConcat_DefaultSeparator_UsesOneArgumentSyntax()
    {
        var command = Compile(
            "SELECT GROUP_CONCAT(name) FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL);

        Assert.Contains("GROUP_CONCAT", command.Sql, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain(command.Parameters, parameter => Equals(parameter.Value, ","));
    }

    [Fact]
    public void Compile_StringAggCustomSeparator_ToMySql_FailsBeforeLowering()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT STRING_AGG(name, '|') FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL));

        Assert.Contains("custom separator", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_GroupConcatInWhere_IsRejectedAsAggregatePlacement()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT name FROM users WHERE GROUP_CONCAT(name) = 'x'",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL));

        Assert.Contains("Aggregate function 'GROUP_CONCAT'", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void Parse_OracleBareSysdate_FailsInsteadOfBecomingColumnReference()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery("SELECT SYSDATE FROM dual", SqlAgentToolType.Oracle));

        Assert.Contains("SYSDATE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_SysdateCall_IsNotAdvertisedAsPortableAlias(SqlAgentToolType provider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SYSDATE() FROM users",
            provider,
            provider));

        Assert.Contains("SYSDATE", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not registered", ex.Message, StringComparison.OrdinalIgnoreCase);
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
