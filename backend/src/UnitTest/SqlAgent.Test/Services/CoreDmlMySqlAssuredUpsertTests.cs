using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlMySqlAssuredUpsertTests
{
    private static readonly SqlProviderCapabilityProfile MySql819 = new(
        SqlAgentToolType.MySQL,
        ServerVersion: new Version(8, 0, 19));

    [Fact]
    public void Compile_SoleEnforcedUniqueTarget_UsesProposedRowAliasOnDuplicateKeyUpdate()
    {
        var command = Compile(
            Upsert(),
            SoleIdAssurance(),
            MySql819);

        Assert.Contains("AS `__core_proposed`", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON DUPLICATE KEY UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`name` = `__core_proposed`.`name`", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VALUES(`name`)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_WithoutStatementUniqueKeyAssurance_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(Upsert(), null, MySql819));

        Assert.Contains("statement assurance", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sole enforced", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_WithAdditionalEnforcedUniqueConflictSource_RemainsFailClosed()
    {
        var assurance = DmlConflictTargetAssurance.FromUniqueKey(
            ["id"],
            "PRIMARY",
            isPrimaryKey: true,
            enforcedUniqueKeyCount: 2,
            hasUnsupportedEnforcedUniqueKeys: false);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(Upsert(), assurance, MySql819));

        Assert.Contains("sole enforced", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any UNIQUE", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_WithUnsupportedEnforcedUniqueConflictSource_RemainsFailClosed()
    {
        var assurance = DmlConflictTargetAssurance.FromUniqueKey(
            ["id"],
            "PRIMARY",
            isPrimaryKey: true,
            enforcedUniqueKeyCount: 1,
            hasUnsupportedEnforcedUniqueKeys: true);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(Upsert(), assurance, MySql819));

        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_AssuredKeyMustEqualCompleteCanonicalTarget()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (tenant_id, id, name) VALUES (7, 1, 'Alice') " +
            "ON CONFLICT (tenant_id, id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);
        var assurance = DmlConflictTargetAssurance.FromUniqueKey(
            ["id"],
            "uq_users_id",
            isPrimaryKey: false,
            enforcedUniqueKeyCount: 1,
            hasUnsupportedEnforcedUniqueKeys: false);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, assurance, MySql819));

        Assert.Contains("complete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unique key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(8, 0, 18)]
    [InlineData(5, 7, 44)]
    public void Compile_Pre819Target_RemainsFailClosedWithoutDeprecatedValuesFallback(
        int major,
        int minor,
        int build)
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(major, minor, build));

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(Upsert(), SoleIdAssurance(), profile));

        Assert.Contains("8.0.19", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deprecated VALUES", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MissingTargetVersion_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                Upsert(),
                SoleIdAssurance(),
                new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL)));

        Assert.Contains("8.0.19", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DoNothing_RemainsFailClosedInsteadOfUsingInsertIgnore()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO NOTHING",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SoleIdAssurance(), MySql819));

        Assert.Contains("INSERT IGNORE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO NOTHING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_RowAliasAvoidsCollisionWithTargetTableName()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO __core_proposed (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, SoleIdAssurance(), MySql819);

        Assert.Contains("AS `__core_proposed_row`", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UniqueKeyResolution_BuildsMySqlAssuranceWithoutImplicitFirebirdPrimaryKeyAssurance()
    {
        var resolution = new DmlUniqueKeyResolution(
            new DatabaseUniqueKeyMetadata("app", "users", "PRIMARY", true, ["id"]),
            [new DatabaseUniqueKeyMetadata("app", "users", "PRIMARY", true, ["id"])]);

        var assurance = resolution.ToConflictTargetAssurance();

        Assert.True(assurance.IsSoleEnforcedUniqueKey);
        Assert.Equal(["id"], assurance.MatchedUniqueKeyColumns);
        Assert.True(assurance.MatchedUniqueKeyIsPrimaryKey);
        Assert.Equal("PRIMARY", assurance.MatchedUniqueKeyName);
        Assert.True(assurance.PrimaryKeyColumns.IsDefaultOrEmpty);
    }

    [Fact]
    public void UniqueKeyAssurance_DoesNotAuthorizeFirebirdPrimaryKeyChannel()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET id = excluded.id, name = excluded.name",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("policy-v1"),
                conflictTargetAssurance: SoleIdAssurance()));

        Assert.Contains("primary key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assurance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedStatement Upsert() => CoreSqlTextParser.ParseDml(
        "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
        "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
        SqlAgentToolType.Postgres);

    private static DmlConflictTargetAssurance SoleIdAssurance() =>
        DmlConflictTargetAssurance.FromUniqueKey(
            ["id"],
            "PRIMARY",
            isPrimaryKey: true,
            enforcedUniqueKeyCount: 1,
            hasUnsupportedEnforcedUniqueKeys: false);

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        DmlConflictTargetAssurance? assurance,
        SqlProviderCapabilityProfile profile) =>
        CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            targetProfile: profile,
            conflictTargetAssurance: assurance);
}
