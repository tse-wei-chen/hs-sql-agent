using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
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

    public static IEnumerable<object[]> QueryBinderParityCases()
    {
        yield return new object[]
        {
            """
            WITH active_users AS (
                SELECT id, role_id
                FROM users
                WHERE active = true
            )
            SELECT u.id
            FROM active_users u
            LEFT JOIN roles r ON r.id = u.role_id
            WHERE EXISTS (
                SELECT 1
                FROM audit_log a
                WHERE a.user_id = u.id
            )
            ORDER BY u.id
            """,
            new[] { "users", "roles", "audit_log" }
        };

        yield return new object[]
        {
            """
            SELECT d.id
            FROM (
                SELECT id
                FROM users
                WHERE id > 0
            ) d
            WHERE d.id < 10
            """,
            new[] { "users" }
        };

        yield return new object[]
        {
            """
            SELECT id FROM users
            UNION ALL
            SELECT user_id FROM audit_log
            ORDER BY 1
            """,
            new[] { "users", "audit_log" }
        };

        yield return new object[]
        {
            """
            SELECT
                u.id,
                (SELECT MAX(a.id) FROM audit_log a WHERE a.user_id = u.id) AS last_audit_id
            FROM users u
            """,
            new[] { "users", "audit_log" }
        };

        yield return new object[]
        {
            """
            SELECT u.id, r.id
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            WHERE r.id > 0
            """,
            new[] { "users", "roles" }
        };
    }

    [Theory]
    [MemberData(nameof(QueryBinderParityCases))]
    public void Facade_FunctionalQueryBinder_MatchesLegacyCompilerOnRichScopes(
        string sql,
        string[] allowedTables)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-binder-v1",
            new HashSet<string>(
                allowedTables,
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 11);

        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
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
        Assert.Equal(legacy.Kind, migrated.Kind);
        Assert.Equal(legacy.TargetProvider, migrated.TargetProvider);
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
    }

    public static IEnumerable<object[]> QueryBinderFailureParityCases()
    {
        yield return new object[]
        {
            "SELECT x.id FROM users u"
        };

        yield return new object[]
        {
            "SELECT a.id FROM users a INNER JOIN roles a ON a.id = a.id"
        };
    }

    [Theory]
    [MemberData(nameof(QueryBinderFailureParityCases))]
    public void Facade_FunctionalQueryBinder_MatchesLegacyBindingFailures(
        string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-binder-failure-v1",
            new HashSet<string>(
                new[] { "users", "roles" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 11);
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler
                .CreateDefault()
                .Compile(
                    parsed,
                    SqlAgentToolType.Postgres,
                    validation,
                    policy));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    public static IEnumerable<object[]> DmlParityCases()
    {
        foreach (var targetProvider in Enum.GetValues<SqlAgentToolType>())
        {
            yield return new object[]
            {
                targetProvider,
                "INSERT INTO users (id, name) VALUES (1, 'a')",
                new[] { "users" }
            };
            yield return new object[]
            {
                targetProvider,
                "UPDATE users SET name = 'b' WHERE id = 1",
                new[] { "users" }
            };
            yield return new object[]
            {
                targetProvider,
                "DELETE FROM users WHERE id = 1",
                new[] { "users" }
            };
        }

        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "UPDATE users SET name = 'b' WHERE id = 1 RETURNING id",
            new[] { "users" }
        };
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "INSERT INTO users (id, name) VALUES (1, 'a') ON CONFLICT (id) DO NOTHING RETURNING id",
            new[] { "users" }
        };

        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "INSERT INTO archive (id, name) SELECT id, name FROM users",
            new[] { "archive", "users" }
        };

        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "UPDATE inventory SET quantity = quantity + 1 FROM warehouse WHERE inventory.id = warehouse.inventory_id",
            new[] { "inventory", "warehouse" }
        };

        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "DELETE FROM inventory USING warehouse WHERE inventory.id = warehouse.inventory_id AND warehouse.region_id = 7",
            new[] { "inventory", "warehouse" }
        };
    }

    [Theory]
    [MemberData(nameof(DmlParityCases))]
    public void Facade_FunctionalDmlPipeline_MatchesLegacyCompiler(
        SqlAgentToolType targetProvider,
        string sql,
        string[] allowedTables)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-dml-typestate-v1",
            new HashSet<string>(
                allowedTables,
                StringComparer.OrdinalIgnoreCase));

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

    public static IEnumerable<object[]> ParserFailureParityCases()
    {
        yield return new object[]
        {
            "SELECT id FROM users WHERE id = @p",
            SqlAgentToolType.Postgres
        };
        yield return new object[]
        {
            "SELECT 1; SELECT 2",
            SqlAgentToolType.Postgres
        };
        yield return new object[]
        {
            "SELECT id FROM users LIMIT 1",
            SqlAgentToolType.MsSqlServer
        };
        yield return new object[]
        {
            "SELECT id FROM users WHERE active = TRUE",
            SqlAgentToolType.MsSqlServer
        };
        yield return new object[]
        {
            "SELECT DATE '2026-08-23'",
            SqlAgentToolType.MsSqlServer
        };
    }

    [Theory]
    [MemberData(nameof(ParserFailureParityCases))]
    public void Facade_FunctionalParserEntry_MatchesLegacyFailures(
        string sql,
        SqlAgentToolType sourceDialect)
    {
        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(sql, sourceDialect));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.ParseQuery(sql, sourceDialect));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_FunctionalParserEntry_MatchesSqlServerTop()
    {
        const string sql =
            "SELECT TOP (5) id FROM users ORDER BY id";

        var validation = new SqlPlanValidationContext(
            "fsharp-parser-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);

        var legacyParsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MsSqlServer);
        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                legacyParsed,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy);

        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Theory]
    [InlineData(
        SqlAgentToolType.Postgres,
        "INSERT INTO users (id, name) VALUES (1, 'a') ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name RETURNING id")]
    [InlineData(
        SqlAgentToolType.Firebird,
        "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'a') MATCHING (id) RETURNING id")]
    public void Facade_FunctionalConflictParser_MatchesLegacyCanonicalAst(
        SqlAgentToolType sourceDialect,
        string sql)
    {
        var legacy = Assert.IsType<InsertStatement>(
            CoreSqlTextParser.ParseDml(sql, sourceDialect).Statement);
        var migrated = Assert.IsType<InsertStatement>(
            SqlCoreFacade.ParseDml(sql, sourceDialect).Statement);

        Assert.NotNull(legacy.Conflict);
        Assert.NotNull(migrated.Conflict);
        Assert.Equal(legacy.Conflict!.Action, migrated.Conflict!.Action);
        Assert.Equal(
            legacy.Conflict.TargetColumns.Select(
                x => string.Join(".", x.Parts.Select(p => p.Value))),
            migrated.Conflict.TargetColumns.Select(
                x => string.Join(".", x.Parts.Select(p => p.Value))));
        Assert.Equal(
            legacy.Conflict.Assignments.Select(
                x => (
                    string.Join(".", x.Column.Parts.Select(p => p.Value)),
                    string.Join(".", x.ProposedColumn.Parts.Select(p => p.Value)))),
            migrated.Conflict.Assignments.Select(
                x => (
                    string.Join(".", x.Column.Parts.Select(p => p.Value)),
                    string.Join(".", x.ProposedColumn.Parts.Select(p => p.Value))));
    }

    [Fact]
    public void Facade_FunctionalConflictParser_MatchesLegacyMySqlFailClosed()
    {
        const string sql =
            "INSERT INTO users (id, name) VALUES (1, 'a') ON DUPLICATE KEY UPDATE name = VALUES(name)";

        var legacy = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MySQL));

        var migrated = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.ParseDml(sql, SqlAgentToolType.MySQL));

        Assert.Equal(legacy.Message, migrated.Message);
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
