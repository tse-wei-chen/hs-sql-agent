using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreLikeEscapeContractTests
{
    [Fact]
    public void Parse_LikeEscape_IsPreservedStructurally()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT name FROM users WHERE name LIKE 'A!_%' ESCAPE '!'",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var like = Assert.IsType<BinaryExpr>(select.Where);

        Assert.Equal("LIKE", like.Operator);
        Assert.Equal("!", like.LikeEscape);
    }

    [Fact]
    public void Parse_NotLikeEscape_PreservesEscapeOnPredicate()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT name FROM users WHERE name NOT LIKE 'A!_%' ESCAPE '!'",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var not = Assert.IsType<UnaryExpr>(select.Where);
        var like = Assert.IsType<BinaryExpr>(not.Operand);

        Assert.Equal("NOT", not.Operator);
        Assert.Equal("LIKE", like.Operator);
        Assert.Equal("!", like.LikeEscape);
    }

    [Fact]
    public void Parse_LikeEscape_RequiresLiteralCharacter()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT name FROM users WHERE name LIKE 'A%' ESCAPE escape_col",
                SqlAgentToolType.Postgres));

        Assert.Contains("ESCAPE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("string literal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!")]
    public void Parse_LikeEscape_RequiresExactlyOneCharacter(string escape)
    {
        var sql = $"SELECT name FROM users WHERE name LIKE 'A%' ESCAPE '{escape}'";

        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres));

        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlNoBackslashEscapes_StillRequiresExplicitEscapeForLike()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT name FROM users WHERE name LIKE 'A%'",
                SqlAgentToolType.MySQL,
                MySqlProfile("NO_BACKSLASH_ESCAPES")));

        Assert.Contains("LIKE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlNoBackslashEscapes_WithExplicitEscape_IsAccepted()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT name FROM users WHERE name LIKE 'A!_%' ESCAPE '!'",
            SqlAgentToolType.MySQL,
            MySqlProfile("NO_BACKSLASH_ESCAPES"));

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var like = Assert.IsType<BinaryExpr>(select.Where);

        Assert.Equal("!", like.LikeEscape);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_LikeEscape_IsEmittedForEveryTargetProvider(SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(
            "SELECT name FROM users WHERE name LIKE 'A!_%' ESCAPE '!'",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.Contains("ESCAPE '!'", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A!_%", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "A!_%"));
    }

    [Fact]
    public void Compile_BackslashEscapeToPostgres_UsesExplicitEscapeStringSyntax()
    {
        var command = CompileQuery(
            "SELECT name FROM users WHERE name LIKE 'A\\_%' ESCAPE '\\'",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Postgres);

        Assert.Contains("ESCAPE E'\\\\'", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "A\\_%"));
    }

    [Fact]
    public void Compile_BackslashEscapeToMySql_DoesNotDependOnTargetSqlMode()
    {
        var command = CompileQuery(
            "SELECT name FROM users WHERE name LIKE 'A\\_%' ESCAPE '\\'",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.MySQL);

        Assert.Contains("ESCAPE CHAR(92)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "A\\_%"));
    }

    [Fact]
    public void CompileDelete_LikeEscape_UsesDmlLoweringContract()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "DELETE FROM users WHERE name LIKE 'A!_%' ESCAPE '!'",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Contains("DELETE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE '!'", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "A!_%"));
    }

    [Fact]
    public void CompileUpdate_LikeEscape_UsesQueryBackedPredicateContract()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "UPDATE users SET display_name = 'Ada' WHERE name LIKE 'A!_%' ESCAPE '!'",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE '!'", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "A!_%"));
    }

    [Fact]
    public void ParseDml_MySqlNoBackslashEscapes_UsesSameExplicitEscapeGate()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "DELETE FROM users WHERE name LIKE 'A%'",
                SqlAgentToolType.MySQL,
                MySqlProfile("NO_BACKSLASH_ESCAPES")));

        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);

        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE name LIKE 'A!_%' ESCAPE '!'",
            SqlAgentToolType.MySQL,
            MySqlProfile("NO_BACKSLASH_ESCAPES"));

        Assert.IsType<DeleteStatement>(parsed.Statement);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

    private static SqlProviderCapabilityProfile MySqlProfile(params string[] modes) =>
        new(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(modes, StringComparer.OrdinalIgnoreCase));
}
