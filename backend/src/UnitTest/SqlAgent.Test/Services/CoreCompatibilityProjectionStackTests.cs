using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCompatibilityProjectionStackTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        foreach (var shape in new[] { "derived", "cte", "scalar", "mixed", "function-query", "outer-join" })
        foreach (var depth in new[] { 128, 512, 2048 })
            yield return new object[] { dialect, shape, depth };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PublicParser_ProjectsQueryExprSourceGraphOn256KiBStack(
        SqlAgentToolType dialect, string shape, int depth)
    {
        var sql = "SELECT id FROM users";
        for (var index = 0; index < depth; index++)
        {
            var boundary = shape == "mixed"
                ? new[] { "derived", "cte", "scalar" }[index % 3]
                : shape;
            sql = boundary switch
            {
                "derived" => "SELECT d.id FROM (" + sql + ") AS d",
                "cte" => "WITH c AS (" + sql + ") SELECT id FROM c",
                "scalar" => "SELECT (" + sql + ") AS id",
                "function-query" => "SELECT COALESCE((" + sql + "), 0) AS id",
                "outer-join" => "SELECT d.id FROM users u LEFT JOIN (" + sql + ") AS d ON u.id = d.id",
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
        }

        Exception? failure = null;
        object? statement = null;
        var worker = new Thread(() =>
        {
            try
            {
                var parsed = CoreSqlTextParser.ParseQuery(sql, dialect);
                // Exercise both conversion directions on the same constrained
                // thread. The inverse seam is isolated from later provider
                // capability/rendering decisions, which have separate gates.
                statement = ConvertBack(parsed.Statement);
            }
            catch (Exception error)
            {
                failure = error;
            }
        }, 256 * 1024) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(20)), "Public parser/projection worker timed out.");
        Assert.True(failure is null, failure?.ToString());
        Assert.NotNull(statement);
    }

    private static object ConvertBack(SqlStatement statement)
    {
        var adapter = typeof(SqlCoreFacade).Assembly.GetType(
            "HsSqlAgent.SqlCore.Rewrite.RewriteLegacyAstAdapter", throwOnError: true)!;
        try
        {
            return adapter.GetMethod("toParsed", BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static)!.Invoke(null, new object[] { statement })!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    [Fact]
    public void InverseAdapter_RejectsCyclicPublicAstWithTypedResourceDiagnostic()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT 1", SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        select.Select[0].Expression = new SubqueryExpr(select, SourceSpan.Unknown);
        var failure = Assert.Throws<SqlCompilationException>(() => ConvertBack(select));
        Assert.Equal("SQL_COMPILER_AST_CYCLE", failure.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticCategory.Resource, failure.Diagnostic?.Category);
        Assert.Equal(SqlDiagnosticStage.Binding, failure.Diagnostic?.Stage);
    }

    [Fact]
    public void InverseAdapter_PreservesLeftOperandFailureBeforeRightCycle()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT 1", SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        select.Select[0].Expression = new BinaryExpr(
            new LiteralExpr(new object(), SourceSpan.Unknown), "+",
            new SubqueryExpr(select, SourceSpan.Unknown), SourceSpan.Unknown);
        var failure = Assert.Throws<SqlCompilationException>(() => ConvertBack(select));
        Assert.Contains("literal value", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("SQL_COMPILER_AST_CYCLE", failure.Diagnostic?.Code);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void DeepExpressionSuspension_PreservesFirstSyntaxErrorOffset(SqlAgentToolType dialect)
    {
        var expression = "id + ,";
        for (var depth = 0; depth < 512; depth++)
            expression = "id + (- (" + expression + ") + 1)";
        var sql = "SELECT " + expression + " FROM users";
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseQuery(sql, dialect));
        Assert.NotNull(error.Diagnostic);
        Assert.Equal(sql.IndexOf(','), error.Diagnostic.Span?.Start);
        Assert.NotEqual("SQL_COMPILER_STACK_RESERVE", error.Diagnostic.Code);
    }
}
