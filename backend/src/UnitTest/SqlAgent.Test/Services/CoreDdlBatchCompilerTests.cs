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
            "CREATE TABLE g6_stage.a (id INTEGER); ALTER TABLE g6_stage.a ADD COLUMN v INTEGER",
            "CREATE TABLE g6_stage.a (id INTEGER); CREATE INDEX idx_a ON g6_stage.a(id)",
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

    [Fact]
    public void BatchIsNotImplicitlyAcceptedBySingleStatementCompiler()
    {
        var sql = "CREATE TABLE g6_stage.a (id INTEGER); CREATE TABLE g6_stage.b (id INTEGER)";
        Assert.False(SqlCoreFacade.TryCompileDdl(
            sql, Options(SqlAgentToolType.Postgres)).Success);
    }
}
