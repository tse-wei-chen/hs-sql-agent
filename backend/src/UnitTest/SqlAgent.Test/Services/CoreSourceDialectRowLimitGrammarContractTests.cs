using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSourceDialectRowLimitGrammarContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT id FROM users LIMIT ALL OFFSET 2")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT id FROM users LIMIT 2,5")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT id FROM users LIMIT 2,5")]
    [InlineData(SqlAgentToolType.Postgres, "SELECT id FROM users FETCH FIRST 2 ROWS ONLY")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT id FROM users FETCH FIRST 2 ROWS ONLY")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT id FROM users FETCH FIRST 2 ROWS ONLY")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT TOP 2 id FROM users")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT id FROM users ORDER BY id OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY")]
    public void Parse_DeclaredRowLimitForms_RemainAccepted(
        SqlAgentToolType sourceDialect,
        string sql)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, sourceDialect);

        Assert.NotNull(parsed.Statement);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "SELECT id FROM users LIMIT ALL", "LIMIT ALL")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT id FROM users LIMIT ALL", "LIMIT ALL")]
    [InlineData(SqlAgentToolType.Postgres, "SELECT id FROM users LIMIT 2,5", "offset,row_count")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT id FROM users OFFSET 2", "preceding LIMIT")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT id FROM users OFFSET 2", "preceding LIMIT")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT id FROM users FETCH FIRST 2 ROWS ONLY", "FETCH")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT id FROM users FETCH FIRST 2 ROWS ONLY", "FETCH")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT id FROM users OFFSET 2 ROWS", "ORDER BY")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT id FROM users ORDER BY id FETCH NEXT 2 ROWS ONLY", "preceding OFFSET")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT id FROM users ORDER BY id OFFSET 1 ROWS FETCH NEXT 0 ROWS ONLY", "greater than zero")]
    public void Parse_UndeclaredRowLimitForms_RemainFailClosed(
        SqlAgentToolType sourceDialect,
        string sql,
        string expected)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sql, sourceDialect));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
