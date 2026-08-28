using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreMySqlSessionProfileTests
{
    [Fact]
    public void Compile_MySqlPipesWithoutSourceProfile_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(SqlAgentToolType.Postgres, sourceProfile: null));

        Assert.Contains("PIPES_AS_CONCAT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sql_mode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlPipesWithUnrelatedSourceMode_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                SqlAgentToolType.Postgres,
                sourceProfile: MySqlProfile("ANSI_QUOTES")));

        Assert.Contains("PIPES_AS_CONCAT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlPipesWithPipesAsConcat_UsesPortableConcatLowering()
    {
        var postgres = CompileQuery(
            SqlAgentToolType.Postgres,
            sourceProfile: MySqlProfile("PIPES_AS_CONCAT"));
        var mysql = CompileQuery(
            SqlAgentToolType.MySQL,
            sourceProfile: MySqlProfile("PIPES_AS_CONCAT"));

        Assert.Contains("||", postgres.Sql, StringComparison.Ordinal);
        Assert.Contains("CONCAT(", mysql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" || ", mysql.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MySqlPipesWithAnsiMode_AlsoEnablesConcatSemantics()
    {
        var command = CompileQuery(
            SqlAgentToolType.MySQL,
            sourceProfile: MySqlProfile("ANSI"));

        Assert.Contains("CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlPipesAsConcat_UsesSessionSpecificHighPrecedence()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT 1 + 2 || 3 AS value",
            SqlAgentToolType.MySQL,
            MySqlProfile("PIPES_AS_CONCAT"));

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var plus = Assert.IsType<BinaryExpr>(Assert.Single(select.Select).Expression);
        Assert.Equal("+", plus.Operator);
        var concat = Assert.IsType<BinaryExpr>(plus.Right);
        Assert.Equal("||", concat.Operator);
        Assert.Equal(2, Assert.IsType<int>(Assert.IsType<LiteralExpr>(concat.Left).Value));
        Assert.Equal(3, Assert.IsType<int>(Assert.IsType<LiteralExpr>(concat.Right).Value));
    }

    [Fact]
    public void Parse_MySqlDoubleQuotesWithoutSourceProfile_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT \"display_name\" FROM users",
                SqlAgentToolType.MySQL));

        Assert.Contains("ANSI_QUOTES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source profile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlDoubleQuotesWithUnrelatedMode_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT \"display_name\" FROM users",
                SqlAgentToolType.MySQL,
                MySqlProfile("PIPES_AS_CONCAT")));

        Assert.Contains("ANSI_QUOTES", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ANSI_QUOTES")]
    [InlineData("ANSI")]
    public void Parse_MySqlAnsiQuotedIdentifier_WithDeclaredMode_PreservesQuoteIntent(string mode)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT \"display\"\"name\" FROM users",
            SqlAgentToolType.MySQL,
            MySqlProfile(mode));

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var column = Assert.IsType<ColumnExpr>(Assert.Single(select.Select).Expression);
        var part = Assert.Single(column.Name.Parts);

        Assert.Equal("display\"name", part.Value);
        Assert.True(part.WasQuoted);
    }

    [Fact]
    public void Parse_MySqlAnsiQuotedIdentifier_AllowsBacktickAsIdentifierContent()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT \"display`name\" FROM users",
            SqlAgentToolType.MySQL,
            MySqlProfile("ANSI_QUOTES"));

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var column = Assert.IsType<ColumnExpr>(Assert.Single(select.Select).Expression);
        var part = Assert.Single(column.Name.Parts);

        Assert.Equal("display`name", part.Value);
        Assert.True(part.WasQuoted);
    }

    [Fact]
    public void Compile_TargetProfileMode_DoesNotAuthorizeSourceSemantics()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                SqlAgentToolType.MySQL,
                targetProfile: MySqlProfile("PIPES_AS_CONCAT"),
                sourceProfile: null));

        Assert.Contains("PIPES_AS_CONCAT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SourceProfileProviderMismatch_FailsAtTypedBoundary()
    {
        var sourceProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Oracle,
            ServerVersion: new Version(26, 0));

        var error = Assert.Throws<ArgumentException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT first_name || last_name FROM users",
                SqlAgentToolType.MySQL,
                sourceProfile));

        Assert.Contains("Source capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("declares provider Oracle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parser source dialect is MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SourceProfileNegativeCompatibility_PreservesArgumentBoundary()
    {
        var sourceProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            CompatibilityLevel: -1);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 1",
                SqlAgentToolType.MySQL,
                sourceProfile));

        Assert.Equal("sourceProfile", error.ParamName);
        Assert.Contains(
            "Provider compatibility level must be non-negative",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SourceProfileNegativeCompatibility_PreservesCompilationBoundary()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT 1",
            SqlAgentToolType.MySQL) with
        {
            SourceProfile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MySQL,
                CompatibilityLevel: -1)
        };

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(
            "Provider compatibility level must be non-negative",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileDml_MySqlPipesWithSourceProfile_UsesSameSessionSemantics()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET display_name = first_name || last_name WHERE id = 7",
            SqlAgentToolType.MySQL,
            MySqlProfile("PIPES_AS_CONCAT"));

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(7, Convert.ToInt32(command.Parameters[0].Value));
    }

    [Fact]
    public void ParseDml_MySqlAnsiQuotes_UsesSameSessionLexicalContract()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE \"users\" SET \"display_name\" = 'Ada' WHERE \"id\" = 7",
            SqlAgentToolType.MySQL,
            MySqlProfile("ANSI_QUOTES"));

        Assert.IsType<UpdateStatement>(parsed.Statement);
    }

    private static CompiledSqlCommand CompileQuery(
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile = null,
        SqlProviderCapabilityProfile? sourceProfile = null)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT first_name || last_name AS full_name FROM users",
            SqlAgentToolType.MySQL,
            sourceProfile);

        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);
    }

    private static SqlProviderCapabilityProfile MySqlProfile(params string[] modes) =>
        new(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(modes, StringComparer.OrdinalIgnoreCase));
}
