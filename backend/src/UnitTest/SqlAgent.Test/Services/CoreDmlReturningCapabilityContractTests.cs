using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningCapabilityContractTests
{
    [Fact]
    public void Sqlite_ReturningVersionBoundary_AlignsMatrixAndCompiler()
    {
        var oldProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            new Version(3, 34));
        var currentProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            new Version(3, 35));

        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Returning(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                oldProfile)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Translated,
            Returning(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                currentProfile)).Status);

        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);

        Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.Sqlite, oldProfile));

        var command = Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            currentProfile);
        Assert.True(command.ReturnsRows);
    }

    [Fact]
    public void Firebird_ReturningVersionBoundary_AlignsMatrixAndCompiler()
    {
        var oldProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Firebird,
            new Version(4, 0));
        var currentProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Firebird,
            new Version(5, 0));

        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Returning(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Firebird,
                oldProfile)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Translated,
            Returning(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Firebird,
                currentProfile)).Status);

        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET name = 'Alice' WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);

        Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.Firebird, oldProfile));

        var command = Compile(
            parsed,
            SqlAgentToolType.Firebird,
            currentProfile);
        Assert.True(command.ReturnsRows);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void UnsupportedReturningTargets_AlignMatrixAndCompiler(
        SqlAgentToolType targetProvider)
    {
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Returning(SqlCapabilityMatrix.ForProvider(targetProvider)).Status);

        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);

        Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, targetProvider, targetProfile: null));
    }

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) =>
        CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("dml-returning-contract-v1"),
            targetProfile: targetProfile);

    private static SqlCapability Returning(ProviderSqlCapabilities matrix) =>
        Assert.Single(
            matrix.Capabilities,
            item => item.Id == "dml.returning_output");
}
