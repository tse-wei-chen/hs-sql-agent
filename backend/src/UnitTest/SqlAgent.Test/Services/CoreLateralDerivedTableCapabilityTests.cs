using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreLateralDerivedTableCapabilityTests
{
    private const string CorrelatedSql =
        "SELECT u.id, q.id " +
        "FROM users u " +
        "CROSS JOIN LATERAL (SELECT u.id AS id) q";

    private const string NonLateralCorrelatedSql =
        "SELECT u.id, q.id " +
        "FROM users u " +
        "CROSS JOIN (SELECT u.id AS id) q";

    [Fact]
    public void Parse_PostgresLateralDerivedTable_IsPreservedStructurally()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            CorrelatedSql,
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var join = Assert.Single(select.Joins);
        var derived = Assert.IsType<DerivedTableSource>(join.Source);

        Assert.True(derived.IsLateral);
    }

    [Fact]
    public void Compile_PostgresLateral_CanReferencePrecedingFromItem()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            CorrelatedSql,
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, SqlAgentToolType.Postgres);

        Assert.Contains("CROSS JOIN LATERAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("u", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OrdinaryDerivedTable_CannotReferencePrecedingFromItem()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            NonLateralCorrelatedSql,
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Compile(parsed, SqlAgentToolType.Postgres));

        Assert.Contains("unknown table/alias qualifier", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("u", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_LeftJoinLateral_CanReferenceLeftSide()
    {
        var sql =
            "SELECT u.id, q.id " +
            "FROM users u " +
            "LEFT JOIN LATERAL (SELECT u.id AS id) q ON TRUE";

        var command = Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres);

        Assert.Contains("LEFT JOIN LATERAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" ON ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RightJoinLateral_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT a.id FROM alpha a " +
                "RIGHT JOIN LATERAL (SELECT a.id AS id) q ON TRUE",
                SqlAgentToolType.Postgres));

        Assert.Contains("RIGHT/FULL JOIN LATERAL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Postgres92Lateral_FailsAtSourceVersionBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                CorrelatedSql,
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 9, 2)));

        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9.3", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Postgres93Lateral_EmitsNativeSyntax()
    {
        var profile = Profile(SqlAgentToolType.Postgres, 9, 3);
        var parsed = CoreSqlTextParser.ParseQuery(
            CorrelatedSql,
            SqlAgentToolType.Postgres,
            profile);

        var command = Compile(
            parsed,
            SqlAgentToolType.Postgres,
            profile);

        Assert.Contains("LATERAL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Postgres92TargetLateral_FailsBeforeRender()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            CorrelatedSql,
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                parsed,
                SqlAgentToolType.Postgres,
                Profile(SqlAgentToolType.Postgres, 9, 2)));

        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9.3", error.Message, StringComparison.Ordinal);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlLateral_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT q.id FROM LATERAL (SELECT id FROM users) q",
                SqlAgentToolType.MySQL));

        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresLateralToMySql_FailsAtTargetProof()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT q.id FROM LATERAL (SELECT id FROM users) q",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.MySQL));

        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_LateralDerived_TracksVersionAndProviderProof()
    {
        var postgresDefault = Capability(SqlAgentToolType.Postgres, null);
        var postgres92 = Capability(
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, 9, 2));
        var postgres93 = Capability(
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, 9, 3));
        var mysql = Capability(SqlAgentToolType.MySQL, null);
        var sqlServer = Capability(SqlAgentToolType.MsSqlServer, null);

        Assert.Equal(SqlCapabilityStatus.Supported, postgresDefault.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, postgres92.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, postgres93.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, mysql.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, sqlServer.Status);
    }

    private static SqlCapability Capability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            item => item.Id == "select.lateral_derived");

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("lateral-derived-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor) =>
        new(provider, ServerVersion: new Version(major, minor));
}
