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

    [Fact]
    public void Facade_TextDmlPolicy_AllowsExplicitFullTableUpdateLikeLegacy()
    {
        const string sql = "UPDATE users SET name = 'b'";
        var sourceProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres);
        var targetProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres);
        var validation = new SqlPlanValidationContext(
            "fsharp-dml-policy-v1",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));
        var policy = new DmlCompilationPolicy(
            RequireWhereForUpdate: false,
            RequireWhereForDelete: true,
            AllowFullTableUpdate: true,
            AllowFullTableDelete: false);
        var assurance = DmlConflictTargetAssurance.FromPrimaryKey(new[] { "id" });

        var parsed = CoreSqlTextParser.ParseDml(
            sql,
            SqlAgentToolType.Postgres,
            sourceProfile);
        var legacy = CoreDmlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                SqlAgentToolType.Postgres,
                validation,
                policy,
                targetProfile,
                assurance);

        var migrated = SqlCoreFacade.CompileDml(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation,
            policy,
            sourceProfile,
            targetProfile,
            assurance);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Kind, migrated.Kind);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Fact]
    public void Facade_FunctionalSourceProfileRewrite_MatchesLegacyCompiler()
    {
        const string sql =
            "SELECT first_name || last_name AS full_name FROM users";

        var sourceProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(
                new[] { "PIPES_AS_CONCAT" },
                StringComparer.OrdinalIgnoreCase));
        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Postgres);
        var validation = new SqlPlanValidationContext(
            "fsharp-source-profile-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);

        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MySQL,
            sourceProfile);
        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                SqlAgentToolType.Postgres,
                validation,
                policy,
                targetProfile);

        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres,
            validation,
            policy,
            sourceProfile,
            targetProfile);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.DoesNotContain(
            "__CORE_MYSQL_PIPES_AS_CONCAT__",
            migrated.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Facade_TextQuery_MySqlPipesWithoutSourceProfile_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "SELECT first_name || last_name AS full_name FROM users";
        var validation = new SqlPlanValidationContext(
            "fsharp-mysql-pipes-failclosed-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();

        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MySQL);
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
                SqlAgentToolType.MySQL,
                SqlAgentToolType.Postgres,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_TextQuery_MySqlPipesProfile_PreservesHighPrecedenceLikeLegacy()
    {
        const string sql =
            "SELECT 1 + 2 || 3 AS value";
        var sourceProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(
                new[] { "PIPES_AS_CONCAT" },
                StringComparer.OrdinalIgnoreCase));
        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Postgres);
        var validation = new SqlPlanValidationContext(
            "fsharp-mysql-pipes-precedence-v1");
        var policy = new SqlExecutionPlanPolicy();

        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.MySQL,
            sourceProfile);
        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                SqlAgentToolType.Postgres,
                validation,
                policy,
                targetProfile);

        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres,
            validation,
            policy,
            sourceProfile,
            targetProfile);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Fact]
    public void Facade_FunctionalSourceProfileValidation_MatchesLegacyCompilationFailure()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT 1",
            SqlAgentToolType.MySQL) with
        {
            SourceProfile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.Oracle)
        };
        var validation = new SqlPlanValidationContext(
            "fsharp-source-profile-failure-v1");
        var policy = new SqlExecutionPlanPolicy();

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
                parsed,
                SqlAgentToolType.Postgres,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_FunctionalCteColumnAliasRewrite_MatchesLegacyCompiler()
    {
        const string sql =
            "WITH u(x) AS (SELECT id FROM users) SELECT x FROM u";

        var validation = new SqlPlanValidationContext(
            "fsharp-cte-alias-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
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
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Theory]
    [InlineData("WITH u(x, y) AS (SELECT id FROM users) SELECT x FROM u")]
    [InlineData("WITH u(x) AS (SELECT * FROM users) SELECT x FROM u")]
    public void Facade_FunctionalCteColumnAliasRewrite_MatchesLegacyFailure(
        string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-cte-alias-failure-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
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

    [Theory]
    [InlineData("SELECT COUNT(*)", SqlAgentToolType.Oracle)]
    [InlineData("SELECT 1 AS x ORDER BY x", SqlAgentToolType.Firebird)]
    public void Facade_FunctionalNoFromValidator_MatchesLegacySuccess(
        string sql,
        SqlAgentToolType targetProvider)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-no-from-v1");
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
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
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Theory]
    [InlineData("SELECT *")]
    [InlineData("SELECT 1 AS x, 2 AS x ORDER BY x")]
    public void Facade_FunctionalNoFromValidator_MatchesLegacyFailure(
        string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-no-from-failure-v1");
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler
                .CreateDefault()
                .Compile(
                    parsed,
                    SqlAgentToolType.Oracle,
                    validation,
                    policy));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Oracle,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Theory]
    [InlineData("SELECT id FROM users ORDER BY id LIMIT 100", 7)]
    [InlineData("SELECT id FROM users ORDER BY id LIMIT 3", 7)]
    public void Facade_FunctionalExecutionPolicy_MatchesLegacyLimitClamp(
        string sql,
        int maxRows)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-execution-policy-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: maxRows);
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
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Fact]
    public void Facade_FunctionalRootCteSetTailRewrite_MatchesLegacyCompiler()
    {
        const string sql =
            "WITH ids AS (SELECT id FROM users) " +
            "SELECT id FROM ids UNION ALL SELECT id FROM ids " +
            "ORDER BY 1 LIMIT 5";

        var validation = new SqlPlanValidationContext(
            "fsharp-root-cte-set-tail-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
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
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Fact]
    public void Facade_TextQuery_SqlServerConcatWithoutRuntimeProof_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "SELECT first_name || last_name AS full_name FROM users";
        var validation = new SqlPlanValidationContext(
            "fsharp-sqlserver-concat-proof-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler
                .CreateDefault()
                .Compile(
                    parsed,
                    SqlAgentToolType.MsSqlServer,
                    validation,
                    policy));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_TextQuery_SqlServer14ConcatProof_UsesPlusLikeLegacy()
    {
        const string sql =
            "SELECT first_name || last_name AS full_name FROM users";
        var sourceProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Postgres);
        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(14, 0),
            CompatibilityLevel: 140);
        var validation = new SqlPlanValidationContext(
            "fsharp-sqlserver-concat-plus-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres,
            sourceProfile);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy,
                targetProfile);

        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy,
            sourceProfile,
            targetProfile);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains(" + ", migrated.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" || ", migrated.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Facade_TextQuery_SqlServer17ConcatProof_UsesNativePipesLikeLegacy()
    {
        const string sql =
            "SELECT first_name || last_name AS full_name FROM users";
        var sourceProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Postgres);
        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(17, 0),
            CompatibilityLevel: 170);
        var validation = new SqlPlanValidationContext(
            "fsharp-sqlserver-concat-native-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres,
            sourceProfile);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy,
                targetProfile);

        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy,
            sourceProfile,
            targetProfile);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains(" || ", migrated.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Facade_TextQuery_FullJoinToMySql_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "SELECT u.id FROM users AS u FULL JOIN archived AS a ON a.id = u.id";
        var validation = new SqlPlanValidationContext(
            "fsharp-join-mysql-target-v1",
            new HashSet<string>(
                new[] { "users", "archived" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MySQL,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_TextQuery_RightJoinToSqliteWithoutProfile_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "SELECT u.id FROM users AS u RIGHT JOIN archived AS a ON a.id = u.id";
        var validation = new SqlPlanValidationContext(
            "fsharp-join-sqlite-target-v1",
            new HashSet<string>(
                new[] { "users", "archived" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Sqlite,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_TextQuery_RightJoinToSqlite39_UsesNativeJoinLikeLegacy()
    {
        const string sql =
            "SELECT u.id FROM users AS u RIGHT JOIN archived AS a ON a.id = u.id";
        var sourceProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres);
        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 39));
        var validation = new SqlPlanValidationContext(
            "fsharp-join-sqlite39-target-v1",
            new HashSet<string>(
                new[] { "users", "archived" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres,
            sourceProfile);

        var legacy = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            validation,
            policy,
            targetProfile);
        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Sqlite,
            validation,
            policy,
            sourceProfile,
            targetProfile);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains("RIGHT JOIN", migrated.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Facade_TextQuery_MySqlFullJoinSource_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "SELECT u.id FROM users AS u FULL JOIN archived AS a ON a.id = u.id";
        var validation = new SqlPlanValidationContext(
            "fsharp-join-mysql-source-v1",
            new HashSet<string>(
                new[] { "users", "archived" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.MySQL,
                SqlAgentToolType.Postgres,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_TextQuery_NestedRightJoinToSqliteWithoutProfile_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "SELECT x.id FROM (SELECT a.id FROM alpha AS a RIGHT JOIN beta AS b ON a.id = b.id) AS x";
        var validation = new SqlPlanValidationContext(
            "fsharp-join-nested-target-v1",
            new HashSet<string>(
                new[] { "alpha", "beta" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Sqlite,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_TextDml_InsertSelectRightJoinToSqliteWithoutProfile_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "INSERT INTO archive (id) SELECT a.id FROM alpha AS a RIGHT JOIN beta AS b ON a.id = b.id";
        var validation = new SqlPlanValidationContext(
            "fsharp-join-dml-target-v1",
            new HashSet<string>(
                new[] { "archive", "alpha", "beta" },
                StringComparer.OrdinalIgnoreCase));
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                validation));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileDml(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Sqlite,
                validation));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_FunctionalProviderProfileRewrite_MatchesLegacySqlServerConcat()
    {
        const string sql =
            "WITH names AS (" +
            "SELECT first_name || last_name AS full_name FROM users" +
            ") SELECT full_name FROM names";

        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(14, 0),
            CompatibilityLevel: 140);
        var validation = new SqlPlanValidationContext(
            "fsharp-provider-profile-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy,
                targetProfile);

        var migrated = SqlCoreFacade.CompileQuery(
            parsed,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy,
            targetProfile);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains(" + ", migrated.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" || ", migrated.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Facade_FunctionalProviderProfileValidation_MatchesLegacyFailure()
    {
        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Oracle);
        var validation = new SqlPlanValidationContext(
            "fsharp-provider-profile-failure-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT 1",
            SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler
                .CreateDefault()
                .Compile(
                    parsed,
                    SqlAgentToolType.Postgres,
                    validation,
                    policy,
                    targetProfile));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                parsed,
                SqlAgentToolType.Postgres,
                validation,
                policy,
                targetProfile));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    public static IEnumerable<object[]> NullOrderingParityCases()
    {
        yield return new object[]
        {
            SqlAgentToolType.MySQL,
            "SELECT u.id FROM users u ORDER BY u.id NULLS LAST"
        };
        yield return new object[]
        {
            SqlAgentToolType.MySQL,
            "SELECT u.id FROM users u ORDER BY u.id NULLS FIRST"
        };
        yield return new object[]
        {
            SqlAgentToolType.MsSqlServer,
            "SELECT u.id FROM users u ORDER BY u.id DESC NULLS FIRST"
        };
    }

    [Theory]
    [MemberData(nameof(NullOrderingParityCases))]
    public void Facade_FunctionalNullOrderingRewrite_MatchesLegacyCompiler(
        SqlAgentToolType targetProvider,
        string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-null-ordering-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
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
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    [Fact]
    public void Facade_FunctionalNullOrderingRewrite_MatchesLegacySetTailFailure()
    {
        const string sql =
            "SELECT id FROM users UNION ALL SELECT id FROM users ORDER BY id NULLS LAST";
        var validation = new SqlPlanValidationContext(
            "fsharp-null-ordering-failure-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler
                .CreateDefault()
                .Compile(
                    parsed,
                    SqlAgentToolType.MySQL,
                    validation,
                    policy));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    public static IEnumerable<object[]> QueryValidatorFailureParityCases()
    {
        yield return new object[]
        {
            "SELECT id FROM users",
            SqlAgentToolType.Postgres,
            new[] { "roles" }
        };
        yield return new object[]
        {
            "SELECT COUNT(DISTINCT *) FROM users",
            SqlAgentToolType.Postgres,
            new[] { "users" }
        };
        yield return new object[]
        {
            "SELECT name FROM users WHERE name ILIKE 'a%'",
            SqlAgentToolType.MySQL,
            new[] { "users" }
        };
    }

    [Theory]
    [MemberData(nameof(QueryValidatorFailureParityCases))]
    public void Facade_FunctionalQueryValidator_MatchesLegacyFailures(
        string sql,
        SqlAgentToolType targetProvider,
        string[] allowedTables)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-query-validator-v1",
            new HashSet<string>(
                allowedTables,
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);
        var parsed = CoreSqlTextParser.ParseQuery(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler
                .CreateDefault()
                .Compile(
                    parsed,
                    targetProvider,
                    validation,
                    policy));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                targetProvider,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    public static IEnumerable<object[]> DmlValidatorFailureParityCases()
    {
        yield return new object[]
        {
            "INSERT INTO archive (id, name) SELECT id FROM users",
            new[] { "archive", "users" }
        };
        yield return new object[]
        {
            "INSERT INTO archive (id) SELECT * FROM users",
            new[] { "archive", "users" }
        };
        yield return new object[]
        {
            "INSERT INTO users (id) VALUES (other_id)",
            new[] { "users" }
        };
    }

    [Theory]
    [MemberData(nameof(DmlValidatorFailureParityCases))]
    public void Facade_FunctionalDmlValidator_MatchesLegacyFailures(
        string sql,
        string[] allowedTables)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-dml-validator-v1",
            new HashSet<string>(
                allowedTables,
                StringComparer.OrdinalIgnoreCase));

        var parsed = CoreSqlTextParser.ParseDml(
            sql,
            SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreDmlCompiler
                .CreateDefault()
                .Compile(
                    parsed,
                    SqlAgentToolType.Postgres,
                    validation));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileDml(
                sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                validation));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    public static IEnumerable<object[]> DmlGrammarFailureParityCases()
    {
        yield return new object[]
        {
            "INSERT INTO users (id, id) VALUES (1, 2)",
            SqlAgentToolType.Postgres
        };
        yield return new object[]
        {
            "UPDATE users u SET name = 'b'",
            SqlAgentToolType.Postgres
        };
        yield return new object[]
        {
            "UPDATE users SET name = 'b' FROM roles WHERE users.role_id = roles.id",
            SqlAgentToolType.MySQL
        };
        yield return new object[]
        {
            "DELETE FROM users USING roles WHERE users.role_id = roles.id",
            SqlAgentToolType.MySQL
        };
        yield return new object[]
        {
            "DELETE FROM users WHERE id = 1 RETURNING *, id",
            SqlAgentToolType.Postgres
        };
        yield return new object[]
        {
            "UPDATE users SET created_at = CAST('not-a-date' AS DATE)",
            SqlAgentToolType.Postgres
        };
    }

    [Theory]
    [MemberData(nameof(DmlGrammarFailureParityCases))]
    public void Facade_FunctionalDmlGrammar_MatchesLegacyFailures(
        string sql,
        SqlAgentToolType sourceDialect)
    {
        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseDml(
                sql,
                sourceDialect));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.ParseDml(
                sql,
                sourceDialect));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
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
        "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'a') MATCHING (id)")]
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
                    string.Join(".", x.ProposedColumn.Parts.Select(p => p.Value)))));
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
    public void Facade_FunctionalCommaFromNormalizer_MatchesLegacyCompiler()
    {
        const string sql =
            "SELECT u.id, r.id FROM users u, roles r WHERE r.id = u.role_id";

        var validation = new SqlPlanValidationContext(
            "fsharp-comma-from-v1",
            new HashSet<string>(
                new[] { "users", "roles" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                CoreSqlTextParser.ParseQuery(
                    sql,
                    SqlAgentToolType.Postgres),
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

    public static IEnumerable<object[]> QueryGrammarCompileParityCases()
    {
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "SELECT id FROM users ORDER BY id LIMIT 3 OFFSET 1"
        };
        yield return new object[]
        {
            SqlAgentToolType.MySQL,
            "SELECT id FROM users ORDER BY id LIMIT 1, 3"
        };
        yield return new object[]
        {
            SqlAgentToolType.MsSqlServer,
            "SELECT id FROM users ORDER BY id OFFSET 1 ROWS FETCH NEXT 3 ROWS ONLY"
        };
        yield return new object[]
        {
            SqlAgentToolType.Oracle,
            "SELECT id FROM users ORDER BY id OFFSET 1 ROWS FETCH NEXT 3 ROWS ONLY"
        };
        yield return new object[]
        {
            SqlAgentToolType.Firebird,
            "SELECT id FROM users ORDER BY id OFFSET 1 ROWS FETCH NEXT 3 ROWS ONLY"
        };
    }

    [Theory]
    [MemberData(nameof(QueryGrammarCompileParityCases))]
    public void Facade_FunctionalQueryGrammar_MatchesLegacyRowTails(
        SqlAgentToolType sourceDialect,
        string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-query-grammar-v1",
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                CoreSqlTextParser.ParseQuery(sql, sourceDialect),
                sourceDialect,
                validation,
                policy);

        var migrated = SqlCoreFacade.CompileQuery(
            sql,
            sourceDialect,
            sourceDialect,
            validation,
            policy);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    public static IEnumerable<object[]> QueryGrammarFailureParityCases()
    {
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "WITH RECURSIVE x AS (SELECT id FROM users) SELECT id FROM x"
        };
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "SELECT d.id FROM (SELECT id FROM users)"
        };
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "SELECT u.id FROM users u CROSS JOIN roles r ON r.id = u.role_id"
        };
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "SELECT u.id FROM users u JOIN roles r USING (id)"
        };
        yield return new object[]
        {
            SqlAgentToolType.Postgres,
            "SELECT id FROM users INTERSECT ALL SELECT user_id FROM audit_log"
        };
        yield return new object[]
        {
            SqlAgentToolType.MsSqlServer,
            "SELECT id FROM users OFFSET 1 ROWS"
        };
    }

    [Theory]
    [MemberData(nameof(QueryGrammarFailureParityCases))]
    public void Facade_FunctionalQueryGrammar_MatchesLegacyFailures(
        SqlAgentToolType sourceDialect,
        string sql)
    {
        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(sql, sourceDialect));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.ParseQuery(sql, sourceDialect));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    public static IEnumerable<object[]> ExpressionGrammarCompileParityCases()
    {
        yield return new object[]
        {
            "SELECT CASE id WHEN 1 THEN CAST(id AS DECIMAL(10,2)) ELSE 0 END FROM users WHERE name NOT LIKE 'A%' ESCAPE '!' AND id IN (1, 2, 3)"
        };
        yield return new object[]
        {
            "SELECT DATE '2026-08-23', TIMESTAMP '2026-08-23 12:34:56', -1, id::bigint FROM users"
        };
        yield return new object[]
        {
            "SELECT COALESCE(name, 'x'), EXTRACT(YEAR FROM created_at) FROM users"
        };
        yield return new object[]
        {
            "SELECT id FROM users WHERE id BETWEEN 1 AND 10 AND EXISTS (SELECT 1 FROM audit_log a WHERE a.user_id = users.id)"
        };
    }

    [Theory]
    [MemberData(nameof(ExpressionGrammarCompileParityCases))]
    public void Facade_FunctionalExpressionGrammar_MatchesLegacyCompiler(
        string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-expression-grammar-v1",
            new HashSet<string>(
                new[] { "users", "audit_log" },
                StringComparer.OrdinalIgnoreCase));
        var policy = new SqlExecutionPlanPolicy(QueryMaxRows: 20);

        var legacy = CoreSqlCompiler
            .CreateDefault()
            .Compile(
                CoreSqlTextParser.ParseQuery(
                    sql,
                    SqlAgentToolType.Postgres),
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
        Assert.Equal(
            legacy.Parameters.Select(p => p.Value?.GetType()).ToArray(),
            migrated.Parameters.Select(p => p.Value?.GetType()).ToArray());
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
    }

    public static IEnumerable<object[]> ExpressionGrammarFailureParityCases()
    {
        yield return new object[]
        {
            "SELECT -id FROM users"
        };
        yield return new object[]
        {
            "SELECT id FROM users WHERE name LIKE 'A%' ESCAPE 'xx'"
        };
        yield return new object[]
        {
            "SELECT CASE END FROM users"
        };
        yield return new object[]
        {
            "SELECT CAST(id AS DECIMAL(MAX,2)) FROM users"
        };
    }

    [Theory]
    [MemberData(nameof(ExpressionGrammarFailureParityCases))]
    public void Facade_FunctionalExpressionGrammar_MatchesLegacyFailures(
        string sql)
    {
        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(
                sql,
                SqlAgentToolType.Postgres));

        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.ParseQuery(
                sql,
                SqlAgentToolType.Postgres));

        Assert.Equal(legacy.GetType(), migrated.GetType());
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
