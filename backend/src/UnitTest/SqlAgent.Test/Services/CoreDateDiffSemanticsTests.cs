using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDateDiffSemanticsTests
{
    private const string Start = "2026-01-01 23:59:59";
    private const string End = "2026-01-02 00:00:00";

    [Fact]
    public void Compile_SqlServerDayBoundaryToMySql_TruncatesBothOperandsToDate()
    {
        var command = CompileQuery(
            $"SELECT DATEDIFF(day, '{Start}', '{End}')",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MySQL);

        Assert.Contains("TIMESTAMPDIFF", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Start, command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(End, command.Sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Count(parameter =>
            Equals(parameter.Value, Start) || Equals(parameter.Value, End)));
    }

    [Fact]
    public void Compile_SqlServerDayBoundaryToSqlite_UsesDateOnlyIntegerDifference()
    {
        var command = CompileQuery(
            $"SELECT DATEDIFF(day, '{Start}', '{End}')",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Sqlite);

        Assert.Contains("JULIANDAY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS INTEGER", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlDateDiffToOracle_RemovesTimeOfDayBeforeSubtracting()
    {
        var command = CompileQuery(
            $"SELECT DATEDIFF('{End}', '{Start}')",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Oracle);

        Assert.Contains("TRUNC", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AS DATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MySqlDateDiffSameDialect_DoesNotUseRawTimestampDiffOperands()
    {
        var command = CompileQuery(
            $"SELECT DATEDIFF('{End}', '{Start}')",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL);

        Assert.Contains("TIMESTAMPDIFF", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_CrossDialectNonDayBoundaryDifference_FailsBeforeLowering()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            $"SELECT DATEDIFF(hour, '{Start}', '{End}')",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MySQL));

        Assert.Contains("Cross-dialect DATEDIFF", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HOUR", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DAY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerNonDayDifferenceSameDialect_PreservesNativeBoundaryFunction()
    {
        var command = CompileQuery(
            $"SELECT DATEDIFF(hour, '{Start}', '{End}')",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("DATEDIFF(HOUR", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdNonDayDifferenceSameDialect_PreservesNativeBoundaryFunction()
    {
        var command = CompileQuery(
            $"SELECT DATEDIFF(hour, '{Start}', '{End}')",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird);

        Assert.Contains("DATEDIFF(HOUR FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DeletePredicate_UsesSamePortableDaySemanticsAsQueryLowering()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                $"DELETE FROM events WHERE DATEDIFF(day, '{Start}', '{End}') = 1",
                SqlAgentToolType.MsSqlServer),
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Contains("DELETE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JULIANDAY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS INTEGER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1));
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
}
