using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFSharpFacadeInteropTests
{
    [Fact]
    public void Facade_QueryTextPipeline_CompilesParameterizedCommand()
    {
        const string sql = "SELECT id FROM users WHERE id = 1 ORDER BY id";
        var validation = new SqlPlanValidationContext(
            "fsharp-query-boundary-v2",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation,
            new SqlExecutionPlanPolicy(20));

        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" = 1", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Facade_DmlTextPipeline_CompilesParameterizedCommand()
    {
        const string sql = "UPDATE users SET name = 'b' WHERE id = 1";
        var validation = new SqlPlanValidationContext(
            "fsharp-dml-boundary-v2",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        var command = SqlCoreFacade.CompileDml(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation);

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'b'", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "b"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Facade_QueryTextPipeline_EnforcesWhitelist()
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-whitelist-v2",
            new HashSet<string>(new[] { "public.users" }, StringComparer.OrdinalIgnoreCase));

        Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM public.secrets",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                validation,
                new SqlExecutionPlanPolicy()));
    }

    [Fact]
    public void Facade_PublicApi_DoesNotExposeFSharpImplementationTypes()
    {
        var assembly = typeof(SqlCoreFacade).Assembly;
        foreach (var type in assembly.GetExportedTypes())
        {
            AssertClrFriendly(type.BaseType);

            foreach (var constructor in type.GetConstructors())
            foreach (var parameter in constructor.GetParameters())
                AssertClrFriendly(parameter.ParameterType);

            foreach (var property in type.GetProperties())
                AssertClrFriendly(property.PropertyType);

            foreach (var method in type.GetMethods(
                         BindingFlags.Public |
                         BindingFlags.Instance |
                         BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                AssertClrFriendly(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                    AssertClrFriendly(parameter.ParameterType);
            }
        }
    }

    private static void AssertClrFriendly(Type? type)
    {
        if (type is null)
            return;

        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            AssertClrFriendly(type.GetElementType());
            return;
        }

        if (type.IsGenericType)
        {
            Assert.DoesNotContain(
                "Microsoft.FSharp",
                type.GetGenericTypeDefinition().FullName ?? type.Name,
                StringComparison.Ordinal);
            foreach (var argument in type.GetGenericArguments())
                AssertClrFriendly(argument);
            return;
        }

        Assert.DoesNotContain(
            "Microsoft.FSharp",
            type.FullName ?? type.Name,
            StringComparison.Ordinal);
    }
}
