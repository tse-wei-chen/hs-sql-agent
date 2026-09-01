using HsSqlAgent.SqlCore;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreFetchPercentCapabilityTests
{
    private const string Sql =
        "SELECT id FROM users ORDER BY id FETCH FIRST 12.5 PERCENT ROWS ONLY";

    [Fact]
    public void Parse_OracleFetchPercent_IsPreservedStructurally()
    {
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Oracle, profile);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Null(select.Limit);
        Assert.Equal(12.5m, select.FetchPercent);
        Assert.False(select.FetchWithTies);
        Assert.Single(select.OrderBy);
    }

    [Fact]
    public void Compile_Oracle121FetchPercent_EmitsNativePercentage()
    {
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Oracle, profile);
        var command = Compile(parsed, SqlAgentToolType.Oracle, profile);

        Assert.Contains("FETCH NEXT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PERCENT ROWS ONLY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Convert.ToDecimal(parameter.Value) == 12.5m);
    }

    [Fact]
    public void Compile_OracleFetchPercentWithTies_PreservesBothSemantics()
    {
        const string sql =
            "SELECT id FROM users ORDER BY id FETCH FIRST 10 PERCENT ROWS WITH TIES";
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle, profile);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(10m, select.FetchPercent);
        Assert.True(select.FetchWithTies);

        var command = Compile(parsed, SqlAgentToolType.Oracle, profile);
        Assert.Contains("PERCENT ROWS WITH TIES", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-10")]
    [InlineData("-12.5")]
    public void Parse_OracleNegativeFetchPercent_NormalizesToZero(string percentage)
    {
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(
            $"SELECT id FROM users ORDER BY id FETCH FIRST {percentage} PERCENT ROWS ONLY",
            SqlAgentToolType.Oracle,
            profile);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(0m, select.FetchPercent);

        var command = Compile(parsed, SqlAgentToolType.Oracle, profile);
        Assert.Contains("PERCENT ROWS ONLY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Convert.ToDecimal(parameter.Value) == 0m);
    }

    [Fact]
    public void Parse_OracleNullFetchPercent_NormalizesToZero()
    {
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users ORDER BY id FETCH FIRST NULL PERCENT ROWS ONLY",
            SqlAgentToolType.Oracle,
            profile);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(0m, select.FetchPercent);

        var command = Compile(parsed, SqlAgentToolType.Oracle, profile);
        Assert.Contains("PERCENT ROWS ONLY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Convert.ToDecimal(parameter.Value) == 0m);
    }

    [Fact]
    public void Parse_OraclePre121FetchPercent_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                Sql,
                SqlAgentToolType.Oracle,
                Profile(SqlAgentToolType.Oracle, 11, 2)));

        Assert.Contains("select.fetch_percent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12.1", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OraclePre121TargetFetchPercent_FailsBeforeRender()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            Sql,
            SqlAgentToolType.Oracle,
            Profile(SqlAgentToolType.Oracle, 12, 1));

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                parsed,
                SqlAgentToolType.Oracle,
                Profile(SqlAgentToolType.Oracle, 11, 2)));

        Assert.Contains("select.fetch_percent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12.1", error.Message, StringComparison.Ordinal);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_PostgresFetchPercent_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users ORDER BY id FETCH FIRST 10 PERCENT ROWS ONLY",
                SqlAgentToolType.Postgres));

        Assert.Contains("select.fetch_percent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Postgres", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleFetchPercentToPostgres_FailsAtTargetProof()
    {
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Oracle, profile);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.Postgres));

        Assert.Contains("select.fetch_percent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Postgres", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FetchPercentWithHardQueryMaxRows_IsPolicyDenied()
    {
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Oracle, profile);

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("fetch-percent-row-cap-v1"),
                new SqlExecutionPlanPolicy(5),
                profile));

        Assert.Contains("QueryMaxRows", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("select.fetch_percent", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationTailFetchPercent_PreservesNativeTail()
    {
        const string sql =
            "SELECT id FROM users UNION ALL SELECT id FROM archived_users " +
            "ORDER BY id FETCH FIRST 5 PERCENT ROWS ONLY";
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle, profile);
        var query = Assert.IsType<QueryStatement>(parsed.Statement);

        Assert.Equal(5m, query.FetchPercent);

        var command = Compile(parsed, SqlAgentToolType.Oracle, profile);
        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PERCENT ROWS ONLY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectFetchPercent_PreservesNestedQuerySemantics()
    {
        const string sql =
            "INSERT INTO archive (id) " +
            "SELECT id FROM users ORDER BY id FETCH FIRST 5 PERCENT ROWS ONLY";
        var profile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Oracle, profile);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("fetch-percent-insert-v1"),
            targetProfile: profile);

        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PERCENT ROWS ONLY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_FetchPercentExpression_RemainsFailClosedUntilTyped()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users ORDER BY id FETCH FIRST (5 + 5) PERCENT ROWS ONLY",
                SqlAgentToolType.Oracle,
                Profile(SqlAgentToolType.Oracle, 12, 1)));

        Assert.Contains("FETCH", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_FetchPercent_IsOracle121Only()
    {
        Assert.Equal(SqlCapabilityStatus.Supported, Capability(SqlAgentToolType.Oracle, null).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(SqlAgentToolType.Oracle, Profile(SqlAgentToolType.Oracle, 11, 2)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Supported,
            Capability(SqlAgentToolType.Oracle, Profile(SqlAgentToolType.Oracle, 12, 1)).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.Postgres, null).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.MySQL, null).Status);
    }

    private static SqlCapability Capability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            item => item.Id == "select.fetch_percent");

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("fetch-percent-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor) =>
        new(provider, ServerVersion: new Version(major, minor));
}
