using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDmlCompilerTests
{
    [Fact]
    public void Compile_Update_ProducesParameterizedCommand()
    {
        var command = Compile(
            "UPDATE public.users SET status = 'disabled' WHERE id = 7",
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" 7", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "disabled"));
        Assert.Contains(command.Parameters, parameter =>
            parameter.Value is int intValue && intValue == 7
            || parameter.Value is long longValue && longValue == 7L);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_Delete_ProducesParameterizedCommand()
    {
        var command = Compile(
            "DELETE FROM public.users WHERE id = 7",
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.Contains("DELETE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter =>
            parameter.Value is int intValue && intValue == 7
            || parameter.Value is long longValue && longValue == 7L);
    }

    [Fact]
    public void Compile_InsertSingleRow_ProducesParameterizedCommand()
    {
        var command = Compile(
            "INSERT INTO public.users (name, age) VALUES ('Alice', 30)",
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }));

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("INSERT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "Alice"));
        Assert.Contains(command.Parameters, parameter =>
            parameter.Value is int intValue && intValue == 30
            || parameter.Value is long longValue && longValue == 30L);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_InsertMultiRow_PreservesOrderedBindings()
    {
        var command = Compile(
            "INSERT INTO public.users (name, age) VALUES ('Alice', 30), ('Bob', 40)",
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Equal(4, command.Parameters.Length);
        Assert.Equal("Alice", command.Parameters[0].Value);
        Assert.Equal(30L, Convert.ToInt64(command.Parameters[1].Value));
        Assert.Equal("Bob", command.Parameters[2].Value);
        Assert.Equal(40L, Convert.ToInt64(command.Parameters[3].Value));
    }

    [Fact]
    public void Compile_InsertSelect_AuthorizesSourceAndUsesStructuredQueryLowering()
    {
        const string sql =
            "INSERT INTO public.archive (id) " +
            "SELECT id FROM public.users WHERE status = 'active'";

        Assert.Throws<UnauthorizedAccessException>(() => Compile(
            sql,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.archive" })));

        var command = Compile(
            sql,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "public.archive",
                    "public.users"
                }));

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "active"));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_UpdateWithoutWhere_IsDeniedByDefault()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            Compile(
                "UPDATE public.users SET status = 'disabled'",
                new SqlPlanValidationContext("policy-v1")));
    }

    [Fact]
    public void Compile_WhitelistViolation_IsDeniedBeforeLowering()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            Compile(
                "DELETE FROM public.secrets WHERE id = 1",
                new SqlPlanValidationContext(
                    "policy-v1",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" })));
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlPlanValidationContext validationContext) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            validationContext);
}
