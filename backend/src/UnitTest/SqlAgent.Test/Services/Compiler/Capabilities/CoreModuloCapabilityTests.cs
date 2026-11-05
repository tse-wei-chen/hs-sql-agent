using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreModuloCapabilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Translated)]
    public void Matrix_MatchesModuloTargetContract(SqlAgentToolType provider, SqlCapabilityStatus expected)
    {
        var capability = Assert.Single(SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "expression.modulo");
        Assert.Equal(expected, capability.Status);
        if (expected == SqlCapabilityStatus.Translated)
            Assert.Contains("MOD(left, right)", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_RawPercentSource_IsRejectedWhereDialectUsesModFunction(SqlAgentToolType sourceDialect)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT amount % 2 FROM orders", sourceDialect, SqlAgentToolType.Postgres));
        Assert.Contains("Operator '%'", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MOD function", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_QueryModulo_UsesModFunctionForTranslatedTargets(SqlAgentToolType targetProvider)
    {
        var command = CompileQuery("SELECT amount % 2 FROM orders", SqlAgentToolType.Postgres, targetProvider);
        Assert.Contains("MOD(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" % ", command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_QueryModulo_PreservesNativePercentOperator(SqlAgentToolType targetProvider)
    {
        var command = CompileQuery("SELECT amount % 2 FROM orders", SqlAgentToolType.Postgres, targetProvider);
        Assert.Contains(" % ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MOD(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_DmlModulo_UsesSameModFunctionContract(SqlAgentToolType targetProvider)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE orders SET amount = amount % 2 WHERE id = 1",
            SqlAgentToolType.Postgres);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed, targetProvider, new SqlPlanValidationContext("modulo-test"), new DmlCompilationPolicy());
        Assert.Contains("MOD(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" % ", command.Sql, StringComparison.Ordinal);
    }

    private static CompiledSqlCommand CompileQuery(string sql, SqlAgentToolType sourceDialect, SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("modulo-test"),
            new SqlExecutionPlanPolicy());
}
