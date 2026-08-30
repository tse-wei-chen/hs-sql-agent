using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningTests
{
    [Theory]
    [InlineData("INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id, name", SqlStatementKind.Insert)]
    [InlineData("UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id, name", SqlStatementKind.Update)]
    [InlineData("DELETE FROM users WHERE id = 1 RETURNING id, name", SqlStatementKind.Delete)]
    public void Compile_PostgresReturning_AppliesAcrossDmlKinds(
        string sql,
        SqlStatementKind expectedKind)
    {
        var command = CompileRaw(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(expectedKind, command.Kind);
        Assert.True(command.ReturnsRows);
        Assert.Contains("RETURNING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"id\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"name\"", command.Sql, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_SqliteReturning_RequiresExplicitSourceAndTargetVersions()
    {
        const string sql = "INSERT INTO users (id) VALUES (1) RETURNING id";
        var sourceError = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite));
        Assert.Contains("3.35", sourceError.Message, StringComparison.OrdinalIgnoreCase);

        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 35));
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite, profile);
        var compiler = CoreDmlCompiler.CreateDefault();

        var targetError = Assert.Throws<SqlCompilationException>(() =>
            compiler.Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("policy-v1")));
        Assert.Contains("3.35", targetError.Message, StringComparison.OrdinalIgnoreCase);

        var command = compiler.Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("policy-v1"),
            targetProfile: profile);

        Assert.True(command.ReturnsRows);
        Assert.Contains("RETURNING", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdReturning_RequiresVersionFiveForPortableMultiRowContract()
    {
        const string sql = "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id";
        var oldProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Firebird,
            ServerVersion: new Version(4, 0));
        var sourceError = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Firebird, oldProfile));
        Assert.Contains("5.0", sourceError.Message, StringComparison.OrdinalIgnoreCase);

        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Firebird,
            new Version(5, 0));
        var command = CompileRaw(
            sql,
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            sourceProfile: profile,
            targetProfile: profile);

        Assert.True(command.ReturnsRows);
        Assert.Contains("RETURNING", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "MySQL")]
    [InlineData(SqlAgentToolType.Oracle, "RETURNING INTO")]
    [InlineData(SqlAgentToolType.MsSqlServer, "trigger")]
    public void Compile_ReturningToUnsupportedTarget_FailsClosed(
        SqlAgentToolType target,
        string expectedMessage)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                target,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ReturningQualifiedColumn_FailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "DELETE FROM users WHERE id = 1 RETURNING users.id",
                SqlAgentToolType.Postgres));

        Assert.Contains("unqualified", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ReturningWildcardCannotMixWithColumns()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "DELETE FROM users WHERE id = 1 RETURNING *, id",
                SqlAgentToolType.Postgres));

        Assert.Contains("cannot be mixed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanFingerprint_ChangesWhenResultRowExecutionModeChanges()
    {
        var command = new CompiledSqlCommand(
            "DELETE FROM users WHERE id = @p0 RETURNING id",
            [new SqlParameterValue("p0", 1)],
            SqlStatementKind.Delete,
            string.Empty,
            SqlAgentToolType.Postgres);
        var normalFingerprint = DmlFingerprintService.ComputePlanFingerprint(command, "policy-v1");
        command.ReturnsRows = true;
        var returningFingerprint = DmlFingerprintService.ComputePlanFingerprint(command, "policy-v1");

        Assert.NotEqual(normalFingerprint, returningFingerprint);
    }

    private static CompiledSqlCommand CompileRaw(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? sourceProfile = null,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, sourceDialect, sourceProfile);
        return CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            targetProfile: targetProfile);
    }
}
