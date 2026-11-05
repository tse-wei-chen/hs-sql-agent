using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreProviderCapabilityProfileTests
{
    [Fact]
    public void Compile_SqlServerRegexWithoutProfile_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileQuery(profile: null));

        Assert.Contains("function.regex_match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compatibility level 170", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRegexBelowCompatibility170_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileQuery(SqlServerProfile(169)));

        Assert.Contains("function.regex_match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compatibility level 170", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRegexAtCompatibility170_EmitsNativeRegexpLike()
    {
        var command = CompileQuery(SqlServerProfile(170));

        Assert.Contains("REGEXP_LIKE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CORE_REGEX_MATCH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal("^A", Assert.IsType<string>(command.Parameters[0].Value));
    }

    [Fact]
    public void CompileDml_SqlServerRegexAtCompatibility170_UsesSameProfileStage()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET flag = 1 WHERE REGEXP_LIKE(name, '^A')",
            SqlAgentToolType.Oracle);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy(),
            SqlServerProfile(170));

        Assert.Contains("REGEXP_LIKE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CORE_REGEX_MATCH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SqlStatementKind.Update, command.Kind);
    }

    [Fact]
    public void Compile_TargetProfileProviderMismatch_FailsBeforeCompilation()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT 1", SqlAgentToolType.Postgres);
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4));

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy(),
                profile));

        Assert.Contains("declares provider MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("targets MsSqlServer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_TargetProfileNegativeCompatibility_PreservesCompilationBoundary()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(SqlServerProfile(-1)));

        Assert.Contains(
            "Provider compatibility level must be non-negative",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profile_SessionMetadataLookup_IsCaseInsensitive()
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string> { "ANSI_QUOTES" },
            SessionSettings: new Dictionary<string, string>
            {
                ["group_concat_max_len"] = "1048576"
            });

        Assert.True(profile.HasSessionMode("ansi_quotes"));
        Assert.Equal("1048576", profile.GetSessionSetting("GROUP_CONCAT_MAX_LEN"));
    }

    private static CompiledSqlCommand CompileQuery(SqlProviderCapabilityProfile? profile)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users WHERE REGEXP_LIKE(name, '^A')",
            SqlAgentToolType.Oracle);

        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            profile);
    }

    private static SqlProviderCapabilityProfile SqlServerProfile(int compatibilityLevel) =>
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(17, 0),
            CompatibilityLevel: compatibilityLevel);
}
