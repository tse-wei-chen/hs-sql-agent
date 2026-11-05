using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqliteUpdateFromCapabilityTests
{
    private const string Sql =
        "UPDATE users SET name = profiles.name FROM profiles WHERE users.id = profiles.user_id";

    [Fact]
    public void Parse_SqliteUpdateFrom_RequiresSourceVersion333()
    {
        var missing = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.Sqlite));
        Assert.Contains("3.33", missing.Message, StringComparison.OrdinalIgnoreCase);

        var oldProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 32));
        var old = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.Sqlite, oldProfile));
        Assert.Contains("3.33", old.Message, StringComparison.OrdinalIgnoreCase);

        var profile = Profile(new Version(3, 33));
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.Sqlite, profile);
        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        Assert.Single(update.FromSources);
    }

    [Fact]
    public void Compile_SqliteUpdateFrom_RequiresTargetVersion333()
    {
        var sourceProfile = Profile(new Version(3, 33));
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.Sqlite, sourceProfile);
        var compiler = CoreDmlCompiler.CreateDefault();

        var missing = Assert.Throws<SqlCompilationException>(() =>
            compiler.Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("sqlite-update-from-target-v1")));
        Assert.Contains("3.33", missing.Message, StringComparison.OrdinalIgnoreCase);

        var old = Assert.Throws<SqlCompilationException>(() =>
            compiler.Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("sqlite-update-from-target-v1"),
                targetProfile: Profile(new Version(3, 32))));
        Assert.Contains("3.33", old.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqliteUpdateFrom_333_RendersNativeClause()
    {
        var profile = Profile(new Version(3, 33));
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.Sqlite, profile);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-update-from-v1"),
            targetProfile: profile);

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profiles", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_SqliteUpdateFrom_CrossProviderRemainsNativeOnly(
        SqlAgentToolType targetProvider)
    {
        var profile = Profile(new Version(3, 33));
        var parsed = CoreSqlTextParser.ParseDml(Sql, SqlAgentToolType.Sqlite, profile);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                targetProvider,
                new SqlPlanValidationContext("sqlite-update-from-cross-v1")));

        Assert.Contains("dml.update.from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqliteUpdateFrom_RequiresTargetVersion333()
    {
        var missing = Capability(SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Sqlite));
        var old = Capability(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Sqlite,
            Profile(new Version(3, 32))));
        var current = Capability(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Sqlite,
            Profile(new Version(3, 33))));

        Assert.Equal(SqlCapabilityStatus.Rejected, missing.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, old.Status);
        Assert.NotEqual(SqlCapabilityStatus.Rejected, current.Status);
        Assert.Contains("3.33", current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlProviderCapabilityProfile Profile(Version version) =>
        new(SqlAgentToolType.Sqlite, ServerVersion: version);

    private static SqlCapability Capability(ProviderSqlCapabilities matrix) =>
        Assert.Single(
            matrix.Capabilities,
            item => item.Id == "dml.update.from");
}
