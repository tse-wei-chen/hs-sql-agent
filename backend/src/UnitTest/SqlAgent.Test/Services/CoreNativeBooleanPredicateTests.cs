using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreNativeBooleanPredicateTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_BareTruePredicate_UsesPortableTruthComparison(
        SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(
            "SELECT id FROM users WHERE TRUE",
            targetProvider);

        Assert.Contains("(1 = 1)", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            command.Parameters,
            parameter => parameter.Value is bool);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_NestedBooleanConstants_PreservePredicateContext(
        SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(
            "SELECT id FROM users WHERE NOT FALSE AND TRUE",
            targetProvider);

        Assert.Contains("(1 = 0)", command.Sql, StringComparison.Ordinal);
        Assert.Contains("(1 = 1)", command.Sql, StringComparison.Ordinal);
        Assert.Contains("NOT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            command.Parameters,
            parameter => parameter.Value is bool);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_CaseWhenBooleanConstant_UsesPredicateTranslation(
        SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(
            "SELECT CASE WHEN TRUE THEN 1 ELSE 0 END AS flag FROM users",
            targetProvider);

        Assert.Contains("CASE WHEN (1 = 1)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            command.Parameters,
            parameter => parameter.Value is bool);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_DeleteBooleanPredicate_UsesSameNativePredicateBoundary(
        SqlAgentToolType targetProvider)
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "DELETE FROM users WHERE FALSE",
                SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("native-boolean-predicate-v1"),
            new DmlCompilationPolicy());

        Assert.Contains("WHERE (1 = 0)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            command.Parameters,
            parameter => parameter.Value is bool);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("native-boolean-predicate-v1"),
            new SqlExecutionPlanPolicy());
}
