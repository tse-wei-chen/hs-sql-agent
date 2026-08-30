using HsSqlAgent.SqlCore;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSourceLexicalContractTests
{
    [Fact]
    public void Compile_MySqlDashDashWithoutSeparator_RemainsArithmetic()
    {
        var command = CompileQuery("SELECT 1--2 AS value", SqlAgentToolType.MySQL);

        Assert.Equal(2, command.Parameters.Length);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt64(parameter.Value) == 1L);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt64(parameter.Value) == -2L);
        Assert.Contains("-", command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT 1 -- comment\n")]
    [InlineData("SELECT 1 # comment\n")]
    public void Compile_MySqlDeclaredLineComments_RemainComments(string sql)
    {
        var command = CompileQuery(sql, SqlAgentToolType.MySQL);

        var parameter = Assert.Single(command.Parameters);
        Assert.Equal(1L, Convert.ToInt64(parameter.Value));
    }

    [Theory]
    [InlineData("SELECT E'line\\nnext'", "line\nnext")]
    [InlineData("SELECT $$O'Brien$$", "O'Brien")]
    public void Parse_PostgresDeclaredStringForms_PreserveDecodedValue(string sql, string expected)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var literal = Assert.IsType<LiteralExpr>(Assert.Single(select.Select).Expression);

        Assert.Equal(expected, Assert.IsType<string>(literal.Value));
    }

    [Fact]
    public void Parse_OracleQString_PreservesDecodedValue()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT q'[O'Brien]'",
            SqlAgentToolType.Oracle);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var literal = Assert.IsType<LiteralExpr>(Assert.Single(select.Select).Expression);

        Assert.Equal("O'Brien", Assert.IsType<string>(literal.Value));
    }

    [Fact]
    public void Parse_SqlServerTempTable_PreservesHashPrefixedIdentifier()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM #temp",
            SqlAgentToolType.MsSqlServer);
        var facts = SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Contains(
            facts.ReferencedTables,
            table => string.Equals(table, "#temp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT 1 # comment")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT $$value$$")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT q'[value]'")]
    public void Parse_ProviderSpecificLexicalForms_AreRejectedByOtherDialects(
        SqlAgentToolType provider,
        string sql)
    {
        Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseQuery(sql, provider));
    }

    [Fact]
    public void Parse_UnterminatedPostgresDollarQuote_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT $tag$unterminated",
                SqlAgentToolType.Postgres));

        Assert.Contains("dollar-quoted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("span", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(string sql, SqlAgentToolType provider) =>
        SqlCoreFacade.CompileQuery(
            sql,
            provider,
            provider,
            new SqlPlanValidationContext("source-lexical-contract-v2"),
            new SqlExecutionPlanPolicy());
}
