using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreJoinCapabilityContractTests
{
    private static readonly Version SqliteJoinMinimumVersion = new(3, 39);
    private static readonly Version SqliteOldVersion = new(3, 38);

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Rejected)]
    public void Matrix_FullJoinCapability_TracksProviderAndRuntimeBoundary(
        SqlAgentToolType provider,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "join.full");

        Assert.Equal(expectedStatus, capability.Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated)]
    public void Matrix_RightJoinCapability_TracksProviderAndRuntimeBoundary(
        SqlAgentToolType provider,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "join.right");

        Assert.Equal(expectedStatus, capability.Status);
    }

    [Theory]
    [InlineData("join.right")]
    [InlineData("join.full")]
    public void Matrix_SqliteJoinCapability_RequiresVersion39Profile(string capabilityId)
    {
        var oldCapability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                SqliteProfile(SqliteOldVersion)).Capabilities,
            item => item.Id == capabilityId);
        var supportedCapability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                SqliteProfile(SqliteJoinMinimumVersion)).Capabilities,
            item => item.Id == capabilityId);

        Assert.Equal(SqlCapabilityStatus.Rejected, oldCapability.Status);
        Assert.Equal(SqlCapabilityStatus.Translated, supportedCapability.Status);
        Assert.Contains("3.39", supportedCapability.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RIGHT", "join.right")]
    [InlineData("FULL", "join.full")]
    public void Compile_ToSqliteWithoutTargetProfile_FailsClosed(
        string joinKind,
        string capabilityId)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                joinKind,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Sqlite));

        Assert.Contains(capabilityId, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.39", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RIGHT", "join.right")]
    [InlineData("FULL", "join.full")]
    public void Compile_ToOldSqliteTarget_FailsClosed(
        string joinKind,
        string capabilityId)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                joinKind,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Sqlite,
                targetProfile: SqliteProfile(SqliteOldVersion)));

        Assert.Contains(capabilityId, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.38", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RIGHT", "RIGHT JOIN")]
    [InlineData("FULL", "FULL OUTER JOIN")]
    public void Compile_ToSqlite39Target_UsesNativeJoin(
        string joinKind,
        string expectedSql)
    {
        var command = CompileQuery(
            joinKind,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Sqlite,
            targetProfile: SqliteProfile(SqliteJoinMinimumVersion));

        Assert.Contains(expectedSql, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RIGHT", "join.right")]
    [InlineData("FULL", "join.full")]
    public void Compile_RawSqliteSourceWithoutProfile_FailsClosed(
        string joinKind,
        string capabilityId)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                joinKind,
                SqlAgentToolType.Sqlite,
                SqlAgentToolType.Postgres));

        Assert.Contains(capabilityId, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source capability profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.39", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RIGHT", "join.right")]
    [InlineData("FULL", "join.full")]
    public void Compile_RawOldSqliteSource_FailsClosed(
        string joinKind,
        string capabilityId)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                joinKind,
                SqlAgentToolType.Sqlite,
                SqlAgentToolType.Postgres,
                sourceProfile: SqliteProfile(SqliteOldVersion)));

        Assert.Contains(capabilityId, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source ServerVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.38", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RIGHT", "RIGHT JOIN")]
    [InlineData("FULL", "FULL OUTER JOIN")]
    public void Compile_RawSqlite39Source_RemainsPortable(
        string joinKind,
        string expectedSql)
    {
        var command = CompileQuery(
            joinKind,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Postgres,
            sourceProfile: SqliteProfile(SqliteJoinMinimumVersion));

        Assert.Contains(expectedSql, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlFullJoinSource_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                "FULL",
                SqlAgentToolType.MySQL,
                SqlAgentToolType.Postgres));

        Assert.Contains("join.full", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source provider MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FullJoinToMySql_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CompileQuery(
                "FULL",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL));

        Assert.Contains("join.full", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NestedRightJoinToSqliteWithoutProfile_FailsClosed()
    {
        var sql =
            "SELECT x.id FROM " +
            "(SELECT a.id FROM alpha AS a RIGHT JOIN beta AS b ON a.id = b.id) AS x";

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("join-profile-nested-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("join.right", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.39", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_InsertSelectRightJoinToSqliteWithoutProfile_FailsClosed()
    {
        var sql =
            "INSERT INTO archive (id) " +
            "SELECT a.id FROM alpha AS a RIGHT JOIN beta AS b ON a.id = b.id";

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres),
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("join-profile-dml-v1")));

        Assert.Contains("join.right", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.39", error.Message, StringComparison.Ordinal);
    }

    private static CompiledSqlCommand CompileQuery(
        string joinKind,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? sourceProfile = null,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        var sql =
            $"SELECT u.id FROM users AS u {joinKind} JOIN archived AS a ON a.id = u.id";
        return CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect, sourceProfile),
            targetProvider,
            new SqlPlanValidationContext("join-capability-contract-v2"),
            new SqlExecutionPlanPolicy(),
            targetProfile);
    }

    private static SqlProviderCapabilityProfile SqliteProfile(Version version) =>
        new(
            SqlAgentToolType.Sqlite,
            ServerVersion: version);
}
