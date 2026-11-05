using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Actual SQLite INTEGER PRIMARY KEY rowid behavior is the independent
/// physical reference for non-executable source-match evidence.
/// No compiled DML command is executed or authorized by this analysis.
/// </summary>
public sealed class SqlCoreSqliteIntegerPkMatchLiveParityTests
{
    private const string Schema =
        "CREATE TABLE dest (id INTEGER PRIMARY KEY, label INTEGER);" +
        "CREATE TABLE lookup (id INTEGER PRIMARY KEY, payload INTEGER);" +
        "CREATE TABLE bag (id INTEGER, payload INTEGER);";

    private static SqlProviderCapabilityProfile Profile(Version version) =>
        new(SqlAgentToolType.Sqlite, version);

    [Fact]
    public void KnownIntegerPrimaryKeyProofMatchesNativeJoinUpperBoundAndUpdate()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        var profile = Profile(Version.Parse(db.ServerVersion));
        var virtualCatalog = SqlCoreFacade.AnalyzeScript(
            Schema, SqlAgentToolType.Sqlite, SqlCatalogState.KnownEmpty(), profile);
        Assert.Empty(virtualCatalog.Diagnostics);
        Assert.True(virtualCatalog.FinalCatalog.IsClosedWorld);

        const string sql = "UPDATE dest SET label = s.payload " +
                           "FROM lookup s WHERE dest.id = s.id RETURNING id, label";
        var stmt = Assert.Single(SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Sqlite, virtualCatalog.FinalCatalog, profile).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.TypeBinding);
        Assert.Equal(SqlMutationSourceMatchCardinality.ProvenAtMostOne,
            stmt.SourceMatchCardinality);

        Execute(db, Schema +
            "INSERT INTO dest(id,label) VALUES (1,0),(2,0),(3,0);" +
            "INSERT INTO lookup(id,payload) VALUES (2,7),(3,8);" +
            "INSERT INTO bag(id,payload) VALUES (2,7),(2,8);");

        using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT dest.id,COUNT(*) " +
                "FROM dest JOIN lookup ON dest.id=lookup.id GROUP BY dest.id";
            using var reader = check.ExecuteReader();
            var affected = new List<int>();
            while (reader.Read())
            {
                Assert.Equal(1L, reader.GetInt64(1));
                affected.Add(reader.GetInt32(0));
            }
            Assert.Equal(new[] { 2, 3 }, affected);
        }

        using (var raw = db.CreateCommand())
        {
            raw.CommandText = sql;
            using var reader = raw.ExecuteReader();
            var updated = new List<string>();
            while (reader.Read()) updated.Add(reader.GetInt32(0) + ":" + reader.GetInt32(1));
            Assert.Equal(new[] { "2:7", "3:8" }, updated.OrderBy(x => x));
        }

        using (var verify = db.CreateCommand())
        {
            verify.CommandText = "SELECT id,label FROM dest ORDER BY id";
            using var reader = verify.ExecuteReader();
            var all = new List<string>();
            while (reader.Read()) all.Add(reader.GetInt32(0) + ":" + reader.GetInt32(1));
            Assert.Equal(new[] { "1:0", "2:7", "3:8" }, all);
        }

        const string duplicateSql =
            "UPDATE dest SET label = b.payload FROM bag b WHERE dest.id = b.id";
        var uncertain = Assert.Single(SqlCoreFacade.AnalyzeScript(
            duplicateSql, SqlAgentToolType.Sqlite,
            virtualCatalog.FinalCatalog, profile).Statements);
        Assert.Equal(SqlMutationSourceMatchCardinality.Unverified,
            uncertain.SourceMatchCardinality);
        using var multiplicity = db.CreateCommand();
        multiplicity.CommandText = "SELECT COUNT(*) FROM dest JOIN bag ON dest.id=bag.id WHERE dest.id=2";
        Assert.Equal(2L, (long)multiplicity.ExecuteScalar()!);
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
