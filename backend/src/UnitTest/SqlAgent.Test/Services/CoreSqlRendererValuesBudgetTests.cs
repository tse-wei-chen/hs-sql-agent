using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlRendererValuesBudgetTests
{
    public static IEnumerable<object[]> SixDialects()
        => Enum.GetValues<SqlAgentToolType>().Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void MultiRowInsert_RendersParametersInRowMajorOrder(SqlAgentToolType dialect)
    {
        var values = string.Join(", ",
            Enumerable.Range(1, 64).Select(i => $"({i}, {i + 1000})"));
        var sql = "INSERT INTO users (id, parent_id) VALUES " + values;
        var result = SqlCoreFacade.CompileDml(
            sql, dialect, dialect,
            new SqlPlanValidationContext("bounded-values-render-v1"));
        var actual = result.Parameters.Select(parameter => Convert.ToInt64(parameter.Value));
        var expected = Enumerable.Range(1, 64).SelectMany(i => new long[] { i, i + 1000 });
        Assert.Equal(expected, actual);
        Assert.NotEmpty(result.Sql);
    }
}
