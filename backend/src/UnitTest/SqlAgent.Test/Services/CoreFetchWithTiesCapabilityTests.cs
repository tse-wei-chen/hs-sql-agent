using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreFetchWithTiesCapabilityTests
{
    private const string Sql =
        "SELECT id FROM users ORDER BY id FETCH FIRST 10 ROWS WITH TIES";

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Parse_FetchWithTies_IsPreservedStructurally(SqlAgentToolType sourceDialect)
    {
        var parsed = CoreSqlTextParser.ParseQuery(Sql, sourceDialect);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.True(select.FetchWithTies);
        Assert.Equal(10, select.Limit);
        Assert.Single(select.OrderBy);
    }

    [Fact]
    public void Compile_Postgres13FetchWithTies_EmitsNativeSemantics()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            Sql,
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, 13, 0));

        var command = Compile(
            parsed,
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, 13, 0));

        Assert.Contains("FETCH FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH TIES", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" LIMIT ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(Convert.ToInt32(parameter.Value), 10));
    }

    [Fact]
    public void Parse_Postgres12FetchWithTies_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                Sql,
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 12, 99)));

        Assert.Contains("select.fetch_with_ties", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Postgres12TargetFetchWithTies_FailsBeforeRender()
    {
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                parsed,
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 12, 99)));

        Assert.Contains("select.fetch_with_ties", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Oracle121FetchWithTies_EmitsNativeSemantics()
    {
        var sourceProfile = Profile(SqlAgentToolType.Oracle, 12, 1);
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Oracle, sourceProfile);

        var command = Compile(
            parsed,
            SqlAgentToolType.Oracle,
            Profile(SqlAgentToolType.Oracle, 12, 1));

        Assert.Contains("FETCH NEXT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH TIES", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, parameter => Equals(Convert.ToInt32(parameter.Value), 10));
    }

    [Fact]
    public void Parse_OraclePre121FetchWithTies_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                Sql,
                SqlAgentToolType.Oracle,
                Profile(SqlAgentToolType.Oracle, 11, 2)));

        Assert.Contains("select.fetch_with_ties", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12.1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OraclePre121TargetFetchWithTies_FailsBeforeRender()
    {
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Oracle);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                parsed,
                SqlAgentToolType.Oracle,
                Profile(SqlAgentToolType.Oracle, 11, 2)));

        Assert.Contains("select.fetch_with_ties", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12.1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FetchWithTiesWithoutOrderBy_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users FETCH FIRST 10 ROWS WITH TIES",
                SqlAgentToolType.Postgres));

        Assert.Contains("WITH TIES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FetchWithTiesToMySql_FailsAtTargetCapabilityProof()
    {
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.MySQL));

        Assert.Contains("select.fetch_with_ties", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FetchWithTiesWithHardQueryMaxRows_IsPolicyDenied()
    {
        var parsed = CoreSqlTextParser.ParseQuery(Sql, SqlAgentToolType.Postgres);

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("fetch-with-ties-row-cap-v1"),
                new SqlExecutionPlanPolicy(5)));

        Assert.Contains("QueryMaxRows", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH TIES", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SetOperationTailFetchWithTies_PreservesTieSemantics()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users UNION ALL SELECT id FROM archived_users ORDER BY id FETCH FIRST 2 ROWS WITH TIES",
            SqlAgentToolType.Postgres);
        var query = Assert.IsType<QueryStatement>(parsed.Statement);

        Assert.True(query.FetchWithTies);

        var command = Compile(parsed, SqlAgentToolType.Postgres);

        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH TIES", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectFetchWithTies_PreservesNestedQuerySemantics()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO archive (id) SELECT id FROM users ORDER BY id FETCH FIRST 2 ROWS WITH TIES",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("fetch-with-ties-insert-v1"));

        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH TIES", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_FetchWithTies_TracksVersionAndProviderProof()
    {
        var postgresDefault = Capability(SqlAgentToolType.Postgres, null);
        var postgresOld = Capability(
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, 12, 99));
        var postgres13 = Capability(
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, 13, 0));
        var oracleOld = Capability(
            SqlAgentToolType.Oracle,
            Profile(SqlAgentToolType.Oracle, 11, 2));
        var oracle121 = Capability(
            SqlAgentToolType.Oracle,
            Profile(SqlAgentToolType.Oracle, 12, 1));
        var mysql = Capability(SqlAgentToolType.MySQL, null);

        Assert.Equal(SqlCapabilityStatus.Supported, postgresDefault.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, postgresOld.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, postgres13.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, oracleOld.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, oracle121.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, mysql.Status);
    }

    private static SqlCapability Capability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            item => item.Id == "select.fetch_with_ties");

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("fetch-with-ties-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor) =>
        new(provider, ServerVersion: new Version(major, minor));
}
