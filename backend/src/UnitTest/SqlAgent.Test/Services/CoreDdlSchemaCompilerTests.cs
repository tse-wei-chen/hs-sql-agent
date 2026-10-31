using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Core.Compilation;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G1: compiler evidence is not script analysis or Host DDL authorization.
/// These tests must RED before the DDL public contract/pipeline exists.
/// </summary>
public sealed class CoreDdlSchemaCompilerTests
{
    public static IEnumerable<object[]> Providers()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
            yield return [provider];
    }

    private static DdlCompilationOptions Options(SqlAgentToolType provider) =>
        new(provider, provider);

    [Theory]
    [MemberData(nameof(Providers))]
    public void CreateTable_ProducesNativeSqlTypedChangesAndNoExecutionAuthority(SqlAgentToolType provider)
    {
        var sql = provider == SqlAgentToolType.MsSqlServer
            ? "CREATE TABLE widgets (id INT NOT NULL, nickname VARCHAR(40))"
            : provider == SqlAgentToolType.Oracle
                ? "CREATE TABLE widgets (id INTEGER NOT NULL, nickname VARCHAR2(40))"
                : "CREATE TABLE widgets (id INTEGER NOT NULL, nickname VARCHAR(40))";
        var plan = SqlCoreFacade.CompileDdl(sql, Options(provider));

        Assert.Equal(provider, plan.TargetDialect);
        Assert.StartsWith("CREATE TABLE ", plan.Command.Sql, StringComparison.Ordinal);
        Assert.NotEqual(sql, plan.Command.Sql); // rendered from typed operations, never raw SQL pass-through
        Assert.Empty(plan.Command.Parameters);
        Assert.Equal(DdlSchemaOperation.CreateTable, plan.Changes[0].Operation);
        Assert.Equal(DdlPrecondition.RequireObjectAbsent, plan.Changes[0].Precondition);
        Assert.Equal(DdlExecutionRisk.Additive, plan.Risk);
        Assert.True(plan.RequiresLiveValidation);
        Assert.False(plan.AuthorizesExecution);
        Assert.Equal(1, plan.Evidence.SchemaVersion);
        Assert.Equal(64, plan.PlanFingerprint.Length);
        Assert.Equal(plan.PlanFingerprint,
            SqlCoreFacade.CompileDdl("  " + sql.ToLowerInvariant().Replace("varchar2", "VARCHAR2").Replace("varchar", "VARCHAR").Replace("integer", "INTEGER").Replace("int ", "INT ") + "  ",
                Options(provider)).PlanFingerprint);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void AddNullableColumn_PreservesNativeProviderGrammarAndRequiresLiveCheck(SqlAgentToolType provider)
    {
        var sql = provider switch
        {
            SqlAgentToolType.Oracle => "ALTER TABLE widgets ADD (nickname VARCHAR2(40))",
            SqlAgentToolType.MsSqlServer => "ALTER TABLE widgets ADD nickname VARCHAR(40) NULL",
            SqlAgentToolType.Firebird => "ALTER TABLE widgets ADD nickname VARCHAR(40)",
            _ => "ALTER TABLE widgets ADD COLUMN nickname VARCHAR(40)"
        };
        var plan = SqlCoreFacade.CompileDdl(sql, Options(provider));

        Assert.Equal(DdlSchemaOperation.AddNullableColumn, plan.Changes[0].Operation);
        Assert.Equal(DdlPrecondition.RequireTablePresentAndColumnAbsent, plan.Changes[0].Precondition);
        Assert.Equal(DdlExecutionRisk.PotentialLock, plan.Risk);
        Assert.True(plan.RequiresLiveValidation);
        Assert.Contains("ALTER TABLE", plan.Command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", plan.Command.Sql, StringComparison.Ordinal);
        if (provider == SqlAgentToolType.Oracle)
            Assert.Contains("ADD (", plan.Command.Sql, StringComparison.Ordinal);
        if (provider == SqlAgentToolType.MsSqlServer)
            Assert.Contains("ADD [", plan.Command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void CreateIndex_OnlyPlainPhysicalColumns(SqlAgentToolType provider)
    {
        var plan = SqlCoreFacade.CompileDdl(
            "CREATE INDEX idx_nickname ON widgets (nickname, id)", Options(provider));
        Assert.Equal(DdlSchemaOperation.CreateIndex, plan.Changes[0].Operation);
        Assert.Equal(DdlPrecondition.RequireTableAndColumnsPresentAndIndexAbsent, plan.Changes[0].Precondition);
        Assert.True(plan.Impact.MayLockSchema);
        Assert.True(plan.RequiresLiveValidation);
        Assert.Contains("CREATE INDEX", plan.Command.Sql, StringComparison.Ordinal);
        Assert.Contains(",", plan.Command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void UnsafeOrUnprovenDdl_NeverCompiles(SqlAgentToolType provider)
    {
        var rejected = new[]
        {
            "DROP TABLE widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY)",
            "CREATE TABLE widgets (id INTEGER, FOREIGN KEY (id) REFERENCES other (id))",
            "CREATE TABLE widgets AS SELECT id FROM users",
            "CREATE INDEX idx_expr ON widgets ((id+1))",
            "CREATE UNIQUE INDEX idx_id ON widgets (id)",
            "ALTER TABLE widgets DROP COLUMN id",
            "ALTER TABLE widgets ADD id INTEGER NOT NULL",
            "TRUNCATE TABLE widgets",
            "GRANT SELECT ON widgets TO user1",
            "SELECT 1",
            "CREATE TABLE widgets (id INTEGER); DROP TABLE widgets",
            "CREATE TABLE widgets (id INTEGER); SELECT 1",
            "CREATE TABLE widgets (id INTEGER) /* comment */ SELECT 1"
        };
        foreach (var sql in rejected)
        {
            var result = SqlCoreFacade.TryCompileDdl(sql, Options(provider));
            Assert.False(result.Success, $"{provider}: {sql}");
            Assert.Null(result.Value);
            Assert.NotNull(result.ErrorCode);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void CrossDialectAndMalformedInput_FailClosed(SqlAgentToolType provider)
    {
        var other = provider == SqlAgentToolType.Postgres ? SqlAgentToolType.MySQL : SqlAgentToolType.Postgres;
        Assert.False(SqlCoreFacade.TryCompileDdl(
            "CREATE TABLE widgets (id INTEGER)", new DdlCompilationOptions(provider, other)).Success);
        Assert.False(SqlCoreFacade.TryCompileDdl(
            "CREATE TABLE widgets (id INTEGER", Options(provider)).Success);
        var integerType = provider == SqlAgentToolType.MsSqlServer ? "INT" : "INTEGER";
        Assert.Equal(
            SqlCoreFacade.CompileDdl("CREATE TABLE widgets (id " + integerType + ")", Options(provider)).PlanFingerprint,
            SqlCoreFacade.CompileDdl("CREATE TABLE widgets (id " + integerType + ");", Options(provider)).PlanFingerprint);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void FingerprintTiesToSchemaOperationAndProvider(SqlAgentToolType provider)
    {
        var table = SqlCoreFacade.CompileDdl("CREATE TABLE widgets (id " + (provider == SqlAgentToolType.MsSqlServer ? "INT" : "INTEGER") + ")", Options(provider));
        var another = SqlCoreFacade.CompileDdl("CREATE TABLE other_widgets (id INTEGER)", Options(provider));
        Assert.NotEqual(table.PlanFingerprint, another.PlanFingerprint);
        Assert.True(table.Changes[0].ObjectIdentity.Parts.Count >= 1);
        Assert.True(table.Evidence.RequiresPhysicalPreconditions);
    }

    [Fact]
    public void OracleAndMySql_ImplicitCommitIsNotTransactionalRollback()
    {
        foreach (var provider in new[] { SqlAgentToolType.Oracle, SqlAgentToolType.MySQL })
        {
            var plan = SqlCoreFacade.CompileDdl(
                "CREATE INDEX idx_nickname ON widgets (nickname)", Options(provider));
            Assert.Equal(DdlTransactionBehavior.ImplicitCommit, plan.TransactionBehavior);
        }
    }
}
