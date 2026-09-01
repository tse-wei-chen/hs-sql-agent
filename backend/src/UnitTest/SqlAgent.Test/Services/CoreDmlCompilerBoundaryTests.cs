using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlCompilerBoundaryTests
{
    [Fact]
    public void Compile_ParsedUpdate_ProducesTypedCommand()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE public.users SET status = 'disabled' WHERE id = 7",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public void Compile_ParsedUpdateWithoutPredicate_IsDeniedByCorePolicy()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE public.users SET status = 'disabled'",
            SqlAgentToolType.Postgres);

        Assert.Throws<UnauthorizedAccessException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));
    }

    [Fact]
    public void Compile_QueryParsedStatement_IsRejectedByDmlCompiler()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM public.users",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("Unsupported DML statement", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
