using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCompatibilityProjectionStackTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        foreach (var shape in new[] { "derived", "cte", "scalar", "mixed" })
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
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
        }

        Exception? failure = null;
        object? statement = null;
        var worker = new Thread(() =>
        {
            try
            {
                statement = CoreSqlTextParser.ParseQuery(sql, dialect).Statement;
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
}
