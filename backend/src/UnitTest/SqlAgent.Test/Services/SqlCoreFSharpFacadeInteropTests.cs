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
    public void Facade_FunctionalAstAudit_CoversRepresentativeQueryAndDmlShapes()
    {
        var query = SqlCoreFacade.ParseQuery(
            """
            WITH active_users AS (
                SELECT id, role_id
                FROM users
                WHERE active = true
            )
            SELECT
                u.id,
                CASE WHEN r.id IS NULL THEN 0 ELSE 1 END AS has_role
            FROM active_users u
            LEFT JOIN roles r ON r.id = u.role_id
            WHERE u.id IN (SELECT user_id FROM audit_log)
            ORDER BY u.id
            """,
            SqlAgentToolType.Postgres);

        Assert.NotNull(query);

        var insert = SqlCoreFacade.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'a') RETURNING id",
            SqlAgentToolType.Postgres);
        var update = SqlCoreFacade.ParseDml(
            "UPDATE users SET name = 'b' WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);
        var delete = SqlCoreFacade.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);

        Assert.NotNull(insert);
        Assert.NotNull(update);
        Assert.NotNull(delete);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Facade_FunctionalQueryPipeline_MatchesLegacyCompiler(
        SqlAgentToolType targetProvider)
    {
        const string sql =
            "SELECT u.id FROM users u WHERE u.id = 42 ORDER BY u.id";

        var validation = new SqlPlanValidationContext(
            "fsharp-typestate-v1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 7);

        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                targetProvider,
                validation,
                policy);

        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            targetProvider,
            validation,
            policy);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Kind, migrated.Kind);
        Assert.Equal(legacy.TargetProvider, migrated.TargetProvider);
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Equal(legacy.ReturnsRows, migrated.ReturnsRows);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
    }

    public static IEnumerable<object[]> DmlParityCases()
    {
        foreach (var targetProvider in Enum.GetValues<SqlAgentToolType>())
        {
            yield return new object[]
            {
                targetProvider,
                "INSERT INTO users (id, name) VALUES (1, 'a')"
            };
            yield return new object[]
            {
                targetProvider,
                "UPDATE users SET name = 'b' WHERE id = 1"
            };
            yield return new object[]
            {
                targetProvider,
                "DELETE FROM users WHERE id = 1"
            };
        }

        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "UPDATE users SET name = 'b' WHERE id = 1 RETURNING id"
        };
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "INSERT INTO users (id, name) VALUES (1, 'a') ON CONFLICT (id) DO NOTHING RETURNING id"
        };
    }

    [Theory]
    [MemberData(nameof(DmlParityCases))]
    public void Facade_FunctionalDmlPipeline_MatchesLegacyCompiler(
        SqlAgentToolType targetProvider,
        string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-dml-typestate-v1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });

        var parsed = CoreSqlTextParser.ParseDml(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = CoreDmlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                targetProvider,
                validation);

        var migrated = SqlCoreFacade.CompileDml(
            sql,
            SqlAgentToolType.Postgres,
            targetProvider,
            validation);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Kind, migrated.Kind);
        Assert.Equal(legacy.TargetProvider, migrated.TargetProvider);
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Equal(legacy.ReturnsRows, migrated.ReturnsRows);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
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
