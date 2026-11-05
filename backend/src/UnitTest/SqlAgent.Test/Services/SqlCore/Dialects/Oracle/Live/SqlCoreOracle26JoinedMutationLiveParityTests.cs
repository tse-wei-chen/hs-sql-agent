using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Physical Oracle 26ai differential test: SQL Core produces only static
/// source-match evidence; a separate real Oracle engine runs the DML.
/// Every scenario owns distinct physical objects and always drops them:
/// Oracle DDL commits and cannot be cleaned up with a rollback.
/// </summary>
public sealed class SqlCoreOracle26JoinedMutationLiveParityTests
{
    private sealed record Scenario(
        string Name,
        string SourceDdl,
        string? OtherDdl,
        string[] SourceRows,
        string[] OtherRows,
        string Mutation,
        SqlMutationSourceMatchCardinality MatchProof,
        string ExpectedRows,
        int? OracleError = null,
        bool ExpectRejected = false);

    private const string TargetDdl =
        "CREATE TABLE {T}(ID NUMBER NOT NULL PRIMARY KEY, PAYLOAD NUMBER, BUCKET NUMBER)";
    private const string PrimarySource =
        "CREATE TABLE {S}(ID NUMBER NOT NULL PRIMARY KEY, PAYLOAD NUMBER, BRIDGE NUMBER)";
    private const string NonUniqueSource =
        "CREATE TABLE {S}(ID NUMBER, PAYLOAD NUMBER, BRIDGE NUMBER)";
    private const string NullableUniqueSource =
        "CREATE TABLE {S}(ID NUMBER, PAYLOAD NUMBER, BRIDGE NUMBER, CONSTRAINT {C} UNIQUE(ID))";
    private const string CompositeSource =
        "CREATE TABLE {S}(K1 NUMBER, K2 NUMBER, PAYLOAD NUMBER, CONSTRAINT {C} UNIQUE(K1,K2))";
    private const string OtherSource =
        "CREATE TABLE {M}(ID NUMBER NOT NULL PRIMARY KEY, BRIDGE NUMBER)";

    private static readonly Scenario[] Scenarios =
    [
        new("pk_update", PrimarySource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)",
             "INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (3,303,3)"], [],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.ProvenAtMostOne, "1:101,2:20"),
        new("pk_delete", PrimarySource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)"], [],
            "DELETE FROM {T} FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.ProvenAtMostOne, "2:20"),
        new("no_match_pk_update", PrimarySource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (3,303,3)"], [],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.ProvenAtMostOne, "1:10,2:20"),
        new("nonunique_single_update", NonUniqueSource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)"], [],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.Unverified, "1:101,2:20"),
        new("duplicate_update_ora30926", NonUniqueSource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)",
             "INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,202,1)"], [],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.Unverified, "1:10,2:20", 30926),
        // Physical Oracle emits ORA-00918 when an unqualified UPDATE SET
        // target is also an accessible FROM column. Catalog name-binding
        // must reject that ambiguity before claiming proof.
        new("ambiguous_assignment_ora918", PrimarySource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)"], [],
            "UPDATE {T} SET PAYLOAD = s.PAYLOAD FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.Unverified, "1:10,2:20", 918, true),
        new("duplicate_delete_once", NonUniqueSource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)",
             "INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,202,1)"], [],
            "DELETE FROM {T} FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.Unverified, "2:20"),
        new("composite_unique_update", CompositeSource, null,
            ["INSERT INTO {S}(K1,K2,PAYLOAD) VALUES (1,7,101)",
             "INSERT INTO {S}(K1,K2,PAYLOAD) VALUES (1,8,102)"], [],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s WHERE {T}.ID = s.K1 AND s.K2 = 7",
            SqlMutationSourceMatchCardinality.ProvenAtMostOne, "1:101,2:20"),
        new("two_unique_sources_bridge", PrimarySource, OtherSource,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)"],
            ["INSERT INTO {M}(ID,BRIDGE) VALUES (1,1)"],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s, {M} m " +
            "WHERE {T}.ID = s.BRIDGE AND s.BRIDGE = s.ID AND s.ID = m.BRIDGE AND m.BRIDGE = m.ID",
            SqlMutationSourceMatchCardinality.ProvenAtMostOne, "1:101,2:20"),
        new("nullable_unique_nulls_do_not_match", NullableUniqueSource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (NULL,111,1)",
             "INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (NULL,222,1)",
             "INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)"], [],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s WHERE {T}.ID = s.ID",
            SqlMutationSourceMatchCardinality.ProvenAtMostOne, "1:101,2:20"),
        new("unanchored_single_source", PrimarySource, null,
            ["INSERT INTO {S}(ID,PAYLOAD,BRIDGE) VALUES (1,101,1)"], [],
            "UPDATE {T} SET {T}.PAYLOAD = s.PAYLOAD FROM {S} s WHERE s.ID = s.BRIDGE",
            SqlMutationSourceMatchCardinality.Unverified, "1:101,2:101")
    ];

    public static IEnumerable<object[]> Cases =>
        Scenarios.Select(s => new object[] { s.Name });

    [Fact]
    public void ParityCorpusHasElevenIndependentPhysicalScenarios()
    {
        Assert.Equal(11, Scenarios.Length);
        Assert.Equal(11, Scenarios.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(Scenarios, s => s.OracleError == 30926);
        Assert.Contains(Scenarios, s => s.Name == "duplicate_delete_once");
        Assert.Contains(Scenarios, s => s.MatchProof == SqlMutationSourceMatchCardinality.ProvenAtMostOne);
        Assert.Contains(Scenarios, s => s.MatchProof == SqlMutationSourceMatchCardinality.Unverified);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StaticSourceMatchProofAgreesWithRealOracle26DmlOutcomes(string name)
    {
        var scenario = Scenarios.Single(s => s.Name == name);
        // Oracle physical object namespace is schema-wide. Never use caller
        // SQL as an identifier, and never rely on transaction rollback for DDL.
        var prefix = "HSOJ_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var objects = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{T}"] = prefix + "_T",
            ["{S}"] = prefix + "_S",
            ["{M}"] = prefix + "_M",
            ["{C}"] = prefix + "_U"
        };
        string Expand(string sql)
        {
            foreach (var item in objects)
                sql = sql.Replace(item.Key, item.Value, StringComparison.Ordinal);
            return sql;
        }

        var physicalDdl = new List<string>
        {
            Expand(TargetDdl), Expand(scenario.SourceDdl)
        };
        if (scenario.OtherDdl is not null)
            physicalDdl.Add(Expand(scenario.OtherDdl));
        var mutation = Expand(scenario.Mutation);

        // The catalog is derived solely from independently parsed DDL; DML
        // rows are never known to the analyzer and cannot justify a proof.
        var schema = SqlCoreFacade.AnalyzeScript(
            string.Join(";", physicalDdl) + ";",
            SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty(),
            null, TestContext.Current.CancellationToken);
        Assert.Empty(schema.Diagnostics);
        Assert.All(schema.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        Assert.True(schema.FinalCatalog.IsClosedWorld);
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Oracle, ServerVersion: new Version(26, 0));
        var analyzed = SqlCoreFacade.AnalyzeScript(
            mutation + ";", SqlAgentToolType.Oracle,
            schema.FinalCatalog, profile, TestContext.Current.CancellationToken);
        var statement = Assert.Single(analyzed.Statements);
        if (scenario.ExpectRejected)
        {
            Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.CatalogBinding);
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
            Assert.Contains(statement.Diagnostics,
                d => d.Code == "SQL_SCHEMA_UPDATE_ASSIGNMENT_AMBIGUOUS");
            Assert.Equal(SqlMutationSourceMatchCardinality.Unverified,
                statement.SourceMatchCardinality);
        }
        else
        {
            Assert.Empty(statement.Diagnostics);
            Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
            Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
            Assert.Equal(scenario.MatchProof, statement.SourceMatchCardinality);
            Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        }
        Assert.Empty(statement.Changes);
        Assert.Same(schema.FinalCatalog, statement.CatalogAfter);

        await using var db = new OracleConnection(ConnectionString());
        await OpenWhenReady(db);
        try
        {
            foreach (var sql in physicalDdl)
                await Execute(db, sql);

            // The target population is identical across the entire corpus.
            await Execute(db, Expand(
                "INSERT INTO {T}(ID,PAYLOAD,BUCKET) VALUES (1,10,1)"));
            await Execute(db, Expand(
                "INSERT INTO {T}(ID,PAYLOAD,BUCKET) VALUES (2,20,2)"));
            foreach (var sql in scenario.SourceRows.Concat(scenario.OtherRows))
                await Execute(db, Expand(sql));
            Assert.Equal("1:10,2:20", await TargetRows(db, objects["{T}"]));

            if (scenario.OracleError is int errorCode)
            {
                var error = await Assert.ThrowsAsync<OracleException>(
                    () => Execute(db, mutation));
                Assert.Equal(errorCode, error.Number);
                // Oracle must roll back this failed UPDATE statement rather
                // than silently applying one of multiple source values.
            }
            else
            {
                await Execute(db, mutation);
            }
            Assert.Equal(scenario.ExpectedRows,
                await TargetRows(db, objects["{T}"]));
        }
        finally
        {
            // DDL auto-commits, including DDL issued after a failed DML.
            // Probe actual objects: setup might have failed partway through.
            foreach (var suffix in new[] { "{M}", "{S}", "{T}" })
            {
                var table = objects[suffix];
                if (await TableExists(db, table))
                    await Execute(db, "DROP TABLE \"" + table +
                        "\" CASCADE CONSTRAINTS PURGE");
            }
            Assert.False(await TableExists(db, objects["{T}"]));
            Assert.False(await TableExists(db, objects["{S}"]));
            Assert.False(await TableExists(db, objects["{M}"]));
        }
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Real Oracle joined DML parity requires TEST_ORACLE_CONNECTION in CI.");
        Assert.Skip("Real Oracle 26 joined DML parity requires TEST_ORACLE_CONNECTION.");
        throw new InvalidOperationException("Skipped.");
    }

    private static async Task OpenWhenReady(OracleConnection db)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.OpenAsync(ct);
                return;
            }
            catch (OracleException) when (attempt < 60 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private static async Task Execute(OracleConnection db, string sql)
    {
        using var command = new OracleCommand(sql, db);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> TableExists(OracleConnection db, string table)
    {
        using var cmd = new OracleCommand(
            "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :tableName", db);
        cmd.Parameters.Add("tableName", OracleDbType.Varchar2).Value = table;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string> TargetRows(OracleConnection db, string table)
    {
        using var cmd = new OracleCommand(
            "SELECT ID, PAYLOAD FROM \"" + table + "\" ORDER BY ID", db);
        using var reader = await cmd.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var id = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var payload = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            rows.Add(id + ":" + payload);
        }
        return string.Join(",", rows);
    }
}
