using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlServerConcatCapabilityTests
{
    [Fact]
    public void Compile_TargetWithoutProfile_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => CompileQuery(targetProfile: null));

        Assert.Contains("expression.concat", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ServerVersion 14.0+", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONCAT_NULL_YIELDS_NULL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_TargetSqlServer13WithoutSessionProof_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(SqlServerProfile(13, 130)));

        Assert.Contains("expression.concat", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_TargetSqlServer13WithExplicitNullConcatOn_UsesPlus()
    {
        var command = CompileQuery(SqlServerProfile(
            13,
            130,
            concatNullYieldsNull: "ON"));

        Assert.Contains(" + ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_TargetSqlServer14_UsesPlusWithoutSessionGuessing()
    {
        var command = CompileQuery(SqlServerProfile(14, 140));

        Assert.Contains(" + ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_TargetSqlServer17BelowCompatibility170_UsesProvenPlusFallback()
    {
        var command = CompileQuery(SqlServerProfile(17, 160));

        Assert.Contains(" + ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_TargetSqlServer17AtCompatibility170_UsesNativeAnsiPipes()
    {
        var command = CompileQuery(SqlServerProfile(17, 170));

        Assert.Contains(" || ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" + ", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_DmlPredicate_UsesSameSqlServerConcatProfileContract()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE first_name || last_name = 'AB'",
            SqlAgentToolType.Postgres);

        var oldRuntime = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("concat-test"),
            new DmlCompilationPolicy(),
            SqlServerProfile(14, 140));
        var nativeRuntime = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("concat-test"),
            new DmlCompilationPolicy(),
            SqlServerProfile(17, 170));

        Assert.Contains(" + ", oldRuntime.Sql, StringComparison.Ordinal);
        Assert.Contains(" || ", nativeRuntime.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RawSqlServerPipesSource_RemainsFailClosedEvenWith17Profile()
    {
        var sourceProfile = SqlServerProfile(17, 170);
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT first_name || last_name FROM users",
            SqlAgentToolType.MsSqlServer,
            sourceProfile);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("concat-test"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("Raw SQL Server source", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("17.x", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grammar", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_TargetProfile_DoesNotAuthorizeRawSqlServerSourcePipes()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT first_name || last_name FROM users",
            SqlAgentToolType.MsSqlServer);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("concat-test"),
                new SqlExecutionPlanPolicy(),
                SqlServerProfile(17, 170)));

        Assert.Contains("Raw SQL Server source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, null, null, SqlCapabilityStatus.Rejected)]
    [InlineData(13, 130, "ON", SqlCapabilityStatus.Translated)]
    [InlineData(14, 140, null, SqlCapabilityStatus.Translated)]
    [InlineData(17, 160, null, SqlCapabilityStatus.Translated)]
    [InlineData(17, 170, null, SqlCapabilityStatus.Supported)]
    public void Matrix_ReflectsDeclaredSqlServerConcatRuntime(
        int? major,
        int? compatibility,
        string? concatNullYieldsNull,
        SqlCapabilityStatus expected)
    {
        var profile = major is null
            ? null
            : SqlServerProfile(major.Value, compatibility, concatNullYieldsNull);
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer, profile).Capabilities,
            item => item.Id == "expression.concat");

        Assert.Equal(expected, capability.Status);
    }

    private static CompiledSqlCommand CompileQuery(
        SqlProviderCapabilityProfile? targetProfile)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT first_name || last_name FROM users",
            SqlAgentToolType.Postgres);

        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("concat-test"),
            new SqlExecutionPlanPolicy(),
            targetProfile);
    }

    private static SqlProviderCapabilityProfile SqlServerProfile(
        int major,
        int? compatibility,
        string? concatNullYieldsNull = null)
    {
        IReadOnlyDictionary<string, string>? settings = concatNullYieldsNull is null
            ? null
            : new Dictionary<string, string>
            {
                ["CONCAT_NULL_YIELDS_NULL"] = concatNullYieldsNull
            };

        return new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(major, 0),
            CompatibilityLevel: compatibility,
            SessionSettings: settings);
    }
}
