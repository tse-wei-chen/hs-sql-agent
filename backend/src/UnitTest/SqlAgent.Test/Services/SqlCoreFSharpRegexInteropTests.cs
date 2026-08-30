using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFSharpRegexInteropTests
{
    private const string QuerySql =
        "SELECT id FROM users WHERE REGEXP_LIKE(name, '^A')";

    [Fact]
    public void Facade_Regex_OracleSourceToPostgres_MatchesLegacyTildeLowering()
    {
        var validation = Validation("fsharp-regex-postgres-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(QuerySql, SqlAgentToolType.Oracle);

        var legacy = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            validation,
            policy);
        var migrated = SqlCoreFacade.CompileQuery(
            QuerySql,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Postgres,
            validation,
            policy);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains(" ~ ", migrated.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Facade_Regex_NativeFunctionTargets_MatchLegacy(
        SqlAgentToolType targetProvider)
    {
        var validation = Validation("fsharp-regex-native-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(QuerySql, SqlAgentToolType.Oracle);

        var legacy = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            validation,
            policy);
        var migrated = SqlCoreFacade.CompileQuery(
            QuerySql,
            SqlAgentToolType.Oracle,
            targetProvider,
            validation,
            policy);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains("REGEXP_LIKE", migrated.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Facade_Regex_UnsupportedTargets_RemainFailClosedLikeLegacy(
        SqlAgentToolType targetProvider)
    {
        var validation = Validation("fsharp-regex-rejected-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(QuerySql, SqlAgentToolType.Oracle);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                targetProvider,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                QuerySql,
                SqlAgentToolType.Oracle,
                targetProvider,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_Regex_SqlServerWithoutProfile_RemainsFailClosedLikeLegacy()
    {
        var validation = Validation("fsharp-regex-sqlserver-no-profile-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(QuerySql, SqlAgentToolType.Oracle);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                QuerySql,
                SqlAgentToolType.Oracle,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_Regex_SqlServer17Compatibility170_MatchesLegacy()
    {
        var sourceProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle);
        var targetProfile = SqlServerProfile(17, 0, 170);
        var validation = Validation("fsharp-regex-sqlserver170-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(
            QuerySql,
            SqlAgentToolType.Oracle,
            sourceProfile);

        var legacy = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy,
            targetProfile);
        var migrated = SqlCoreFacade.CompileQuery(
            QuerySql,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy,
            sourceProfile,
            targetProfile);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains("REGEXP_LIKE", migrated.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(16, 0, 170)]
    [InlineData(17, 0, 169)]
    public void Facade_Regex_SqlServerInsufficientRuntime_RemainsFailClosedLikeLegacy(
        int major,
        int minor,
        int compatibilityLevel)
    {
        var sourceProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle);
        var targetProfile = SqlServerProfile(major, minor, compatibilityLevel);
        var validation = Validation("fsharp-regex-sqlserver-old-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(
            QuerySql,
            SqlAgentToolType.Oracle,
            sourceProfile);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy,
                targetProfile));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                QuerySql,
                SqlAgentToolType.Oracle,
                SqlAgentToolType.MsSqlServer,
                validation,
                policy,
                sourceProfile,
                targetProfile));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_Regex_PostgresRawSource_RemainsFailClosedLikeLegacy()
    {
        var validation = Validation("fsharp-regex-postgres-source-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(QuerySql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                QuerySql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_Regex_SourceValidationPreservesBinderFailureOrderingLikeLegacy()
    {
        const string sql =
            "SELECT id FROM users u WHERE REGEXP_LIKE(x.name, '^A')";
        var validation = Validation("fsharp-regex-binder-order-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
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

    [Fact]
    public void Facade_Regex_WrongArity_RemainsFailClosedLikeLegacy()
    {
        const string sql =
            "SELECT id FROM users WHERE REGEXP_LIKE(name)";
        var validation = Validation("fsharp-regex-arity-v1");
        var policy = new SqlExecutionPlanPolicy();
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle);

        var legacy = Assert.ThrowsAny<Exception>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Oracle,
                validation,
                policy));
        var migrated = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                sql,
                SqlAgentToolType.Oracle,
                SqlAgentToolType.Oracle,
                validation,
                policy));

        Assert.Equal(legacy.GetType(), migrated.GetType());
        Assert.Equal(legacy.Message, migrated.Message);
    }

    [Fact]
    public void Facade_Regex_DmlSqlServer17Compatibility170_MatchesLegacy()
    {
        const string sql =
            "UPDATE users SET flag = 1 WHERE REGEXP_LIKE(name, '^A')";
        var sourceProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle);
        var targetProfile = SqlServerProfile(17, 0, 170);
        var validation = Validation("fsharp-regex-dml-sqlserver170-v1");
        var policy = new DmlCompilationPolicy();
        var assurance = DmlConflictTargetAssurance.FromPrimaryKey(new[] { "id" });
        var parsed = CoreSqlTextParser.ParseDml(
            sql,
            SqlAgentToolType.Oracle,
            sourceProfile);

        var legacy = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy,
            targetProfile,
            assurance);
        var migrated = SqlCoreFacade.CompileDml(
            sql,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.MsSqlServer,
            validation,
            policy,
            sourceProfile,
            targetProfile,
            assurance);

        Assert.Equal(legacy.Sql, migrated.Sql);
        Assert.Equal(legacy.Parameters.ToArray(), migrated.Parameters.ToArray());
        Assert.Equal(legacy.PlanFingerprint, migrated.PlanFingerprint);
        Assert.Contains("REGEXP_LIKE", migrated.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlPlanValidationContext Validation(string version) =>
        new(
            version,
            new HashSet<string>(
                new[] { "users" },
                StringComparer.OrdinalIgnoreCase));

    private static SqlProviderCapabilityProfile SqlServerProfile(
        int major,
        int minor,
        int compatibilityLevel) =>
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(major, minor),
            CompatibilityLevel: compatibilityLevel);
}
