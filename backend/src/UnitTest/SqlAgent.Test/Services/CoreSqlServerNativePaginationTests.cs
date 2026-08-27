using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlServerNativePaginationTests
{
    [Fact]
    public void Compile_OffsetPagination_DoesNotExposeSyntheticRowNumber()
    {
        var command = Compile(
            "SELECT id FROM users ORDER BY id LIMIT 10 OFFSET 5");

        Assert.Contains("ROW_NUMBER()", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS [id]", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[results_wrapper].[_core_page_0]", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "SELECT * FROM (SELECT",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OffsetPagination_PreservesComputedProjectionAlias()
    {
        var command = Compile(
            "SELECT LOWER(name) AS label FROM users ORDER BY label LIMIT 10 OFFSET 5");

        Assert.Contains("AS [label]", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROW_NUMBER()", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "SELECT * FROM (SELECT",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DistinctOffsetPagination_OrdersThroughProjectedValue()
    {
        var command = Compile(
            "SELECT DISTINCT name FROM users ORDER BY name LIMIT 10 OFFSET 5");

        Assert.Contains("SELECT DISTINCT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROW_NUMBER()", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_core_page_order_", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OffsetWildcardProjection_FailsClosedInsteadOfLeakingSyntheticColumn()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT * FROM users ORDER BY id LIMIT 10 OFFSET 5"));

        Assert.Contains("stable name", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit aliases", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OffsetUnnamedComputedProjection_FailsClosedInsteadOfRenamingOutput()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT LOWER(name) FROM users ORDER BY LOWER(name) LIMIT 10 OFFSET 5"));

        Assert.Contains("stable name", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit aliases", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOffsetPagination_DoesNotExposeSyntheticRowNumber()
    {
        var command = Compile(
            "SELECT id FROM users UNION ALL SELECT id FROM archived_users " +
            "ORDER BY id LIMIT 10 OFFSET 5");

        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROW_NUMBER()", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS [id]", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "SELECT * FROM (SELECT *, ROW_NUMBER()",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOffsetUnnamedOutput_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT LOWER(name) FROM users UNION ALL " +
                "SELECT LOWER(name) FROM archived_users ORDER BY 1 LIMIT 10 OFFSET 5"));

        Assert.Contains("stable name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(string sql) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-native-pagination-v1"),
            new SqlExecutionPlanPolicy());
}
