using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G6/S16 RED: an atomic batch is one ordered, closed F# proof, not a
/// C# loop over individually-authorized SQL or the DML atomic batch.
/// </summary>
public sealed class CoreDdlBatchCompilerTests
{
    private static DdlCompilationOptions Options(SqlAgentToolType provider) =>
        new(provider, provider);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PostgreSqlCreateBatch_HasOrderedImmutablePlanAndSeparateFingerprint(int count)
    {
        var commands = Enumerable.Range(1, count)
            .Select(i => $"CREATE TABLE g6_stage.t{i} (id INTEGER NOT NULL)");
        var script = string.Join(";\n", commands) + ";";
        var batch = SqlCoreFacade.CompileDdlBatch(script, Options(SqlAgentToolType.Postgres));
        Assert.Equal(count, batch.Plans.Count);
        Assert.Equal(count, batch.SourceSpans.Count);
        for (var i = 0; i < count; i++)
        {
            var span = batch.SourceSpans[i];
            Assert.Equal($"CREATE TABLE g6_stage.t{i + 1} (id INTEGER NOT NULL)",
                script.Substring(span.Start, span.Length));
        }
        Assert.Equal(64, batch.PlanFingerprint.Length);
        Assert.True(batch.RequiresLiveValidation);
        Assert.False(batch.AuthorizesExecution);
        Assert.Equal(DdlTransactionBehavior.Unknown, batch.TransactionBehavior);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(DdlSchemaOperation.CreateTable, batch.Plans[i].Changes[0].Operation);
            Assert.Equal(DdlExecutionRisk.Additive, batch.Plans[i].Risk);
            Assert.Equal($"CREATE TABLE \"g6_stage\".\"t{i + 1}\" (\"id\" INTEGER NOT NULL)",
                batch.Plans[i].Command.Sql);
        }
        Assert.Equal(batch.PlanFingerprint, SqlCoreFacade.CompileDdlBatch(
            script, Options(SqlAgentToolType.Postgres)).PlanFingerprint);
        Assert.NotEqual(batch.PlanFingerprint, SqlCoreFacade.CompileDdlBatch(
            string.Join(";\n", commands.Reverse()) + ";",
            Options(SqlAgentToolType.Postgres)).PlanFingerprint);
    }

    [Fact]
    public void PostgreSqlBatchRejectsDependenceCrossSchemaAndMixedStatementFamililies()
    {
        var forbidden = new[]
        {
            "CREATE TABLE g6_stage.a (id INTEGER)", // must be 2 or more
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.a (v INTEGER)",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE other.b (id INTEGER)",
            "CREATE TABLE a (id INTEGER); CREATE TABLE b (id INTEGER)",
            "CREATE TABLE g6_stage.a (id INTEGER); DROP TABLE g6_stage.other",
            "DROP TABLE g6_stage.a; CREATE TABLE g6_stage.b (id INTEGER)",
            "CREATE TABLE g6_stage.a (id INTEGER); INSERT INTO g6_stage.a VALUES (1)",
            "CREATE TABLE g6_stage.a (id INTEGER); SELECT 1",
            "CREATE TABLE g6_stage.a (id INTEGER); BEGIN; CREATE TABLE g6_stage.b (id INTEGER)",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER); SELECT 1",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER) CASCADE",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER); CREATE TABLE g6_stage.c (id INTEGER); CREATE TABLE g6_stage.d (id INTEGER); CREATE TABLE g6_stage.e (id INTEGER)",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER); DROP TABLE g6_stage.a",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER); COMMIT",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER); ; /* malformed */ SELECT 1"
        };
        foreach (var sql in forbidden)
        {
            var outcome = SqlCoreFacade.TryCompileDdlBatch(sql, Options(SqlAgentToolType.Postgres));
            Assert.False(outcome.Success, sql);
            Assert.Null(outcome.Value);
        }

        var valid = "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER)";
        foreach (var other in Enum.GetValues<SqlAgentToolType>().Where(
            p => p != SqlAgentToolType.Postgres))
            Assert.False(SqlCoreFacade.TryCompileDdlBatch(valid, Options(other)).Success, other.ToString());
        Assert.False(SqlCoreFacade.TryCompileDdlBatch(valid, new DdlCompilationOptions(
            SqlAgentToolType.Postgres, SqlAgentToolType.MySQL)).Success);
    }

    [Theory]
    [InlineData("CREATE TABLE g6_stage.a (id INTEGER NOT NULL); ALTER TABLE g6_stage.a ADD COLUMN extra INTEGER NULL", 2)]
    [InlineData("CREATE TABLE g6_stage.a (id INTEGER NOT NULL); ALTER TABLE g6_stage.a ADD COLUMN extra INTEGER NULL; CREATE INDEX idx_extra ON g6_stage.a(extra)", 3)]
    [InlineData("CREATE TABLE g6_stage.a (id INTEGER NOT NULL); ALTER TABLE g6_stage.a ADD COLUMN extra INTEGER NULL; ALTER TABLE g6_stage.a ADD COLUMN more INTEGER NULL; CREATE INDEX idx_extra ON g6_stage.a(extra, more)", 4)]
    public void DependentCreateChain_RequiresClosedOrderedEffects(string sql, int steps)
    {
        var batch = SqlCoreFacade.CompileDdlBatch(sql, Options(SqlAgentToolType.Postgres));
        Assert.Equal(DdlBatchTopology.DependentCreateChain, batch.Topology);
        Assert.False(batch.AuthorizesExecution);
        Assert.Equal(steps, batch.Plans.Count);
        Assert.Equal(steps, batch.SourceSpans.Count);
        Assert.Equal(DdlSchemaOperation.CreateTable, batch.Plans[0].Changes[0].Operation);
        Assert.Equal(DdlSchemaOperation.AddNullableColumn, batch.Plans[1].Changes[0].Operation);
        Assert.All(batch.Plans, p => Assert.Equal(
            batch.Plans[0].Changes[0].ObjectIdentity.Key,
            p.Changes[0].ObjectIdentity.Key));
        Assert.Equal(64, batch.PlanFingerprint.Length);
        Assert.Equal(batch.PlanFingerprint, SqlCoreFacade.CompileDdlBatch(
            sql, Options(SqlAgentToolType.Postgres)).PlanFingerprint);
        var reordering = sql.Split(';').Reverse();
        Assert.False(SqlCoreFacade.TryCompileDdlBatch(string.Join(";", reordering),
            Options(SqlAgentToolType.Postgres)).Success);
        if (steps >= 3 && sql.Contains("CREATE INDEX", StringComparison.Ordinal))
            Assert.Equal(DdlSchemaOperation.CreateIndex, batch.Plans[^1].Changes[0].Operation);
    }

    [Fact]
    public void DependentBatchRejectsMissingOrConflictingSourceEffectsAndUnrelatedObjects()
    {
        var forbidden = new[]
        {
            "ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL; CREATE INDEX ix ON g6_stage.a(v)",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE INDEX ix ON g6_stage.a(id)",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.b ADD COLUMN v INTEGER NULL",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE other.a ADD COLUMN v INTEGER NULL",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN id INTEGER NULL",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL; ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL; CREATE INDEX ix ON g6_stage.a(missing)",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL; CREATE INDEX ix ON g6_stage.a(v); CREATE INDEX iy ON g6_stage.a(id)",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL; DROP TABLE g6_stage.a",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a DROP COLUMN id",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NOT NULL",
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL; CREATE INDEX CONCURRENTLY ix ON g6_stage.a(v)"
        };
        foreach (var sql in forbidden)
            Assert.False(SqlCoreFacade.TryCompileDdlBatch(
                sql, Options(SqlAgentToolType.Postgres)).Success, sql);
        var valid = "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER NULL";
        foreach (var p in Enum.GetValues<SqlAgentToolType>().Where(x => x != SqlAgentToolType.Postgres))
            Assert.False(SqlCoreFacade.TryCompileDdlBatch(valid, Options(p)).Success, p.ToString());
    }

    [Fact]
    public void BatchIsNotImplicitlyAcceptedBySingleStatementCompiler()
    {
        var sql = "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER)";
        Assert.False(SqlCoreFacade.TryCompileDdl(
            sql, Options(SqlAgentToolType.Postgres)).Success);
    }
}
