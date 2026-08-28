using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFSharpFacadeInteropTests
{
    [Fact]
    public void Facade_FromCSharp_ParsesAndCompilesQuery()
    {
        var parsed = SqlCoreFacade.ParseQuery(
            "SELECT id FROM users",
            SqlAgentToolType.Postgres);

        Assert.NotNull(parsed);

        var command = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "fsharp-interop-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new SqlExecutionPlanPolicy(QueryMaxRows: 10));

        Assert.Equal(SqlStatementKind.Query, command.Kind);
        Assert.Contains("users", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Facade_TryMethods_AreClrFriendlyOnSuccessAndFailure()
    {
        var success = SqlCoreFacade.TryCompileQuery(
            "SELECT id FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext(
                "fsharp-interop-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new SqlExecutionPlanPolicy(QueryMaxRows: 10));

        Assert.True(success.Success);
        Assert.NotNull(success.Value);
        Assert.Null(success.ErrorCode);
        Assert.Null(success.ErrorMessage);
        Assert.Empty(success.Diagnostics);

        var failure = SqlCoreFacade.TryParseQuery(
            "SELECT * FROM",
            SqlAgentToolType.Postgres);

        Assert.False(failure.Success);
        Assert.Null(failure.Value);
        Assert.Equal("SQL_PARSE_ERROR", failure.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));
        Assert.Empty(failure.Diagnostics);
    }

    [Fact]
    public void Facade_PublicApi_DoesNotExposeFSharpImplementationTypes()
    {
        var assembly = typeof(SqlCoreFacade).Assembly;
        var exportedTypes = assembly.GetExportedTypes();

        Assert.Contains(typeof(SqlCoreFacade), exportedTypes);

        foreach (var type in exportedTypes)
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
            var genericDefinition = type.GetGenericTypeDefinition();
            Assert.DoesNotContain(
                "Microsoft.FSharp",
                genericDefinition.FullName ?? genericDefinition.Name,
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
