using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFSharpFacadeInteropTests
{
    [Fact]
    public void Facade_QueryTextPipeline_MatchesLegacyRepresentativeQuery()
    {
        const string sql = "SELECT id FROM users WHERE id = 1 ORDER BY id";
        var validation = new SqlPlanValidationContext(
            "fsharp-query-boundary-v1",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);

        var legacy = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            validation,
            policy);
        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation,
            policy);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Fact]
    public void Facade_DmlTextPipeline_MatchesLegacyRepresentativeUpdate()
    {
        const string sql = "UPDATE users SET name = 'b' WHERE id = 1";
        var validation = new SqlPlanValidationContext(
            "fsharp-dml-boundary-v1",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        var legacy = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            validation);
        var migrated = SqlCoreFacade.CompileDml(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
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
