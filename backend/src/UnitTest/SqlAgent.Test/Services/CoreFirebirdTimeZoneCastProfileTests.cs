using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreFirebirdTimeZoneCastProfileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void Compile_TimestampWithTimeZoneCast_FirebirdTargetRequiresVersion4(
        int? majorVersion)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                "SELECT CAST(CURRENT_TIMESTAMP AS TIMESTAMP WITH TIME ZONE)",
                SqlAgentToolType.Postgres,
                majorVersion is null ? null : FirebirdProfile(majorVersion.Value)));

        Assert.Contains(
            "temporal.firebird_time_zone_type",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "TIMESTAMP WITH TIME ZONE",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void Compile_TimeWithTimeZoneCast_FirebirdTargetRequiresVersion4(
        int? majorVersion)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                "SELECT CAST(CURRENT_TIME AS TIME WITH TIME ZONE)",
                SqlAgentToolType.Postgres,
                majorVersion is null ? null : FirebirdProfile(majorVersion.Value)));

        Assert.Contains(
            "temporal.firebird_time_zone_type",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "TIME WITH TIME ZONE",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "SELECT CAST(CURRENT_TIMESTAMP AS TIMESTAMP WITH TIME ZONE)",
        "TIMESTAMP WITH TIME ZONE")]
    [InlineData(
        "SELECT CAST(CURRENT_TIME AS TIME WITH TIME ZONE)",
        "TIME WITH TIME ZONE")]
    public void Compile_Firebird4TimeZoneCast_Compiles(
        string sql,
        string expectedType)
    {
        var command = CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            FirebirdProfile(4));

        Assert.Contains(
            expectedType,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SameDialectFirebirdTimeZoneCast_CannotBypassTargetProfile()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                "SELECT CAST(CURRENT_TIMESTAMP AS TIMESTAMP WITH TIME ZONE)",
                SqlAgentToolType.Firebird,
                targetProfile: null));

        Assert.Contains(
            "temporal.firebird_time_zone_type",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileDml_FirebirdTimeZoneCast_CannotBypassTargetProfile()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE events SET event_time = CAST(CURRENT_TIMESTAMP AS TIMESTAMP WITH TIME ZONE) WHERE id = 1",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("firebird-time-zone-cast-v1"),
                new DmlCompilationPolicy(),
                targetProfile: null));

        Assert.Contains(
            "temporal.firebird_time_zone_type",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? targetProfile) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-time-zone-cast-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile FirebirdProfile(int majorVersion) =>
        new(
            SqlAgentToolType.Firebird,
            ServerVersion: new Version(majorVersion, 0));
}
