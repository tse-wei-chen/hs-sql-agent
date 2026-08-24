using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreMySqlStringAggregateTests
{
    [Fact]
    public void Compile_PostgresStringAggCustomSeparator_UsesMySqlSeparatorClause()
    {
        var command = Compile(
            "SELECT STRING_AGG(name, '|') AS names FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains("GROUP_CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" SEPARATOR '|'", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(", '|')", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MySqlStringAggregateSeparator_PreservesNestedExpressionCommas()
    {
        var command = Compile(
            "SELECT STRING_AGG(COALESCE(name, 'unknown'), '|') FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains("GROUP_CONCAT(COALESCE(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" SEPARATOR '|'", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "unknown"));
    }

    [Fact]
    public void Compile_MySqlStringAggregateSeparator_EscapesQuoteAndCommaLiterally()
    {
        var command = Compile(
            "SELECT STRING_AGG(name, 'a''b,|') FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains(" SEPARATOR 'a''b,|'", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_StringAggregateDynamicSeparator_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT STRING_AGG(name, separator_column) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL));

        Assert.Contains("aggregate.string.dynamic_separator", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlNativeMultiExpressionGroupConcat_IsNotReinterpretedAsSeparator()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT GROUP_CONCAT(first_name, last_name) FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL));

        Assert.Contains("multiple value expressions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STRING_AGG", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlNativeDefaultGroupConcat_RemainsPortableToPostgres()
    {
        var command = Compile(
            "SELECT GROUP_CONCAT(name) FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres);

        Assert.Contains("STRING_AGG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("','", command.Sql, StringComparison.Ordinal);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
