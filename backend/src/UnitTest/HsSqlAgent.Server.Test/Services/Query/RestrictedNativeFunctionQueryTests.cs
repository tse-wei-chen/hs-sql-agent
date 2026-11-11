using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Interfaces;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Table facts cannot see reads or writes hidden inside database-defined functions.
/// Native identifier syntax is allowed in unrestricted Query, but is not proof of
/// the function's physical effects or relation accesses for restricted Query.
/// </summary>
public sealed class RestrictedNativeFunctionQueryTests
{
    private static readonly IReadOnlySet<string> AllowedTables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "app.users", "dbo.users", "main.users", "users"
        };

    private static readonly SecurityPolicyModel Policy = new()
    {
        QueryMaxRows = 20,
        QueryTimeoutSeconds = 10
    };

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "SELECT app.leak_secret() AS leaked FROM app.users")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT dbo.leak_secret() AS leaked FROM dbo.users")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT APP.LEAK_SECRET() AS leaked FROM APP.USERS")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT PKG.LEAK_SECRET() AS leaked FROM users")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT \"leak_secret\"() AS leaked FROM main.users")]
    public void WhitelistedQuery_RejectsOpaqueNativeFunctionIdentities(
        SqlAgentToolType providerType, string sql)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(providerType);

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            new TypedQueryRuntime().Compile(provider.Object, sql, providerType, Policy, AllowedTables));

        Assert.Contains("not proven", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("function", error.Message, StringComparison.OrdinalIgnoreCase);
        var diagnostic = Assert.IsType<SqlDiagnostic>(
            error.Data["HsSqlAgent.SqlCore.Diagnostic"]);
        Assert.Equal("SQL_POLICY_NATIVE_QUERY_FUNCTION_NOT_PROVEN", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "SELECT ABS(id) AS amount FROM app.users")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT ABS(id) AS amount FROM dbo.users")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT ABS(id) AS amount FROM APP.USERS")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT ABS(id) AS amount FROM users")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT ABS(id) AS amount FROM main.users")]
    public void WhitelistedQuery_KeepsOrdinaryCoreBuiltinFunction(
        SqlAgentToolType providerType, string sql)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(providerType);

        var compiled = new TypedQueryRuntime().Compile(
            provider.Object, sql, providerType, Policy, AllowedTables);

        Assert.Contains("ABS(", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "WITH probe AS (SELECT app.leak_secret() AS leaked FROM app.users) SELECT leaked FROM probe")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT id FROM dbo.users WHERE dbo.leak_secret() = 1")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT CASE WHEN APP.LEAK_SECRET() = 1 THEN id ELSE 0 END FROM APP.USERS")]
    public void WhitelistedQuery_CatchesNativeFunctionsInNestedQueryContexts(
        SqlAgentToolType providerType, string sql)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(providerType);

        Assert.Throws<UnauthorizedAccessException>(() =>
            new TypedQueryRuntime().Compile(provider.Object, sql, providerType, Policy, AllowedTables));
    }

    [Fact]
    public void MySqlQueryWithoutWhitelist_RejectsOpaqueNativeFunctionAtPolicyStage()
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.MySQL);

        const string sql = "SELECT app.temp_side_effect(id) AS affected FROM app.users";
        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            new TypedQueryRuntime().Compile(
                provider.Object, sql, SqlAgentToolType.MySQL,
                Policy, allowedTables: null));

        Assert.Contains("not proven", error.Message, StringComparison.OrdinalIgnoreCase);
        var diagnostic = Assert.IsType<SqlDiagnostic>(
            error.Data["HsSqlAgent.SqlCore.Diagnostic"]);
        Assert.Equal("SQL_POLICY_NATIVE_QUERY_FUNCTION_NOT_PROVEN", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new TypedQueryRuntime().CompileWithFacts(
                provider.Object, sql, SqlAgentToolType.MySQL,
                Policy, allowedTables: null, targetProfile: null));
    }

    [Fact]
    public void MySqlQueryWithEmptyAllowset_AlsoEnforcesNativeFunctionEffects()
    {
        // The F# facade deliberately normalizes an empty allowset to 'no
        // whitelist'. Physical Host policy must not treat it differently
        // from null when deciding whether to trust a native stored function.
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.MySQL);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Throws<UnauthorizedAccessException>(() =>
            new TypedQueryRuntime().Compile(
                provider.Object,
                "SELECT app.temp_side_effect(id) FROM app.users",
                SqlAgentToolType.MySQL, Policy, empty));
    }

    [Fact]
    public void StandaloneSqlCore_MySqlNativeFunctionGrammarRetainsDefaultCompatibility()
    {
        const string sql = "SELECT app.temp_side_effect(id) AS affected FROM app.users";
        var regular = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("generic-compiler"),
            new SqlExecutionPlanPolicy(20));
        Assert.Contains("temp_side_effect", regular.Sql, StringComparison.OrdinalIgnoreCase);

        // The physical Query Host opts into the same typed F# Policy at the
        // public facade boundary without altering standalone SqlCore grammar.
        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("host-query-guard"),
                new SqlExecutionPlanPolicy(20, requireProvenMySqlNativeFunctions: true)));
        Assert.IsType<SqlDiagnostic>(error.Data["HsSqlAgent.SqlCore.Diagnostic"]);
    }

    [Fact]
    public void UnrestrictedSqlServerQuery_NativeFunctionNamesRetainExistingCompilerBehavior()
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.MsSqlServer);

        var compiled = new TypedQueryRuntime().Compile(
            provider.Object, "SELECT dbo.leak_secret() AS leaked FROM dbo.users",
            SqlAgentToolType.MsSqlServer, Policy, allowedTables: null);

        Assert.Contains("leak_secret", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
