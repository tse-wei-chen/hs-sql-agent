using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlUpsertCapabilityContractTests
{
    [Fact]
    public void Sqlite_UpsertVersionBoundary_AlignsMatrixAndCompiler()
    {
        var oldProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 23));
        var currentProfile = oldProfile with { ServerVersion = new Version(3, 24) };

        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Upsert(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                oldProfile)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Translated,
            Upsert(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Sqlite,
                currentProfile)).Status);

        var parsed = UpsertStatement();

        Assert.Throws<SqlCompilationException>(() =>
            Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                oldProfile,
                assurance: null));

        var command = Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            currentProfile,
            assurance: null);
        Assert.Contains(
            "ON CONFLICT",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySql_AssuredStatementCanCompileWhileProviderMatrixRemainsRejected()
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 0, 19));
        var assurance = DmlConflictTargetAssurance.FromUniqueKey(
            ["id"],
            "PRIMARY",
            isPrimaryKey: true,
            enforcedUniqueKeyCount: 1,
            hasUnsupportedEnforcedUniqueKeys: false);

        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Upsert(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MySQL,
                profile)).Status);

        var command = Compile(
            UpsertStatement(),
            SqlAgentToolType.MySQL,
            profile,
            assurance);

        Assert.Contains(
            "ON DUPLICATE KEY UPDATE",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Firebird_AssuredStatementCanCompileWhileProviderMatrixRemainsRejected()
    {
        var assurance = DmlConflictTargetAssurance.FromPrimaryKey(["id"]);

        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Upsert(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Firebird)).Status);

        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET id = excluded.id, name = excluded.name",
            SqlAgentToolType.Postgres);

        var command = Compile(
            parsed,
            SqlAgentToolType.Firebird,
            targetProfile: null,
            assurance: assurance);

        Assert.StartsWith(
            "UPDATE OR INSERT INTO",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedStatement UpsertStatement() =>
        CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile,
        DmlConflictTargetAssurance? assurance) =>
        CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("dml-upsert-contract-v1"),
            targetProfile: targetProfile,
            conflictTargetAssurance: assurance);

    private static SqlCapability Upsert(ProviderSqlCapabilities matrix) =>
        Assert.Single(
            matrix.Capabilities,
            item => item.Id == "dml.upsert_merge");
}
