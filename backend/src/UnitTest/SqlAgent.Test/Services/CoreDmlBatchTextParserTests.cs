using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlBatchTextParserTests
{
    [Fact]
    public void ParseDmlBatch_TwoDeletes_PreservesOrder()
    {
        var batch = CoreDmlBatchTextParser.ParseDmlBatch(
            "DELETE FROM order_details WHERE order_id = 7; DELETE FROM orders WHERE id = 7;",
            SqlAgentToolType.Postgres);

        Assert.Equal(2, batch.Count);
        Assert.IsType<DeleteStatement>(batch.Statements[0].Statement);
        Assert.IsType<DeleteStatement>(batch.Statements[1].Statement);
    }

    [Fact]
    public void ParseDmlBatch_SemicolonInsideLiteral_DoesNotSplitStatement()
    {
        var batch = CoreDmlBatchTextParser.ParseDmlBatch(
            "UPDATE notes SET body = 'hello;world' WHERE id = 1; DELETE FROM notes WHERE id = 2",
            SqlAgentToolType.Postgres);

        Assert.Equal(2, batch.Count);
        Assert.IsType<UpdateStatement>(batch.Statements[0].Statement);
        Assert.IsType<DeleteStatement>(batch.Statements[1].Statement);
    }

    [Fact]
    public void ParseDmlBatch_SemicolonInsideComments_DoesNotCreateEmptyStatement()
    {
        var batch = CoreDmlBatchTextParser.ParseDmlBatch(
            "UPDATE users SET active = false WHERE id = 1 /* ; ignored */; -- ; ignored\nDELETE FROM users WHERE id = 2",
            SqlAgentToolType.Postgres);

        Assert.Equal(2, batch.Count);
    }

    [Fact]
    public void ParseDmlBatch_MixedSelect_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreDmlBatchTextParser.ParseDmlBatch(
                "DELETE FROM users WHERE id = 1; SELECT * FROM users",
                SqlAgentToolType.Postgres));

        Assert.Contains("ParseDml", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDmlBatch_TransactionControl_FailsClosed()
    {
        Assert.Throws<SqlParseException>(() =>
            CoreDmlBatchTextParser.ParseDmlBatch(
                "BEGIN; DELETE FROM users WHERE id = 1; COMMIT",
                SqlAgentToolType.Postgres));
    }

    [Fact]
    public void ParseDmlBatch_ConsecutiveTerminators_RejectsEmptyStatement()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreDmlBatchTextParser.ParseDmlBatch(
                "DELETE FROM users WHERE id = 1;;DELETE FROM users WHERE id = 2",
                SqlAgentToolType.Postgres));

        Assert.Contains("empty statement", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
