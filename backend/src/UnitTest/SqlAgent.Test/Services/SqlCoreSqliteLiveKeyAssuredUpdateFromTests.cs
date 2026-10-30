using System.Collections.Immutable;
using System.Data;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Host-minted SQLite metadata (not bare static analysis) can admit precisely
/// one native CTE-prefixed UPDATE FROM source. Runtime schema drift remains a
/// host execution concern; no effect/approval bypass is inferred.
/// </summary>
public sealed class SqlCoreSqliteLiveKeyAssuredUpdateFromTests
{
    private const string Sql =
        "WITH checked AS (SELECT 2 AS id) UPDATE dest SET label = s.payload " +
        "FROM lookup s WHERE dest.id = s.id AND dest.id >= 2 RETURNING id, label";

    private static SqlProviderCapabilityProfile Profile(int minor = 35) =>
        new(SqlAgentToolType.Sqlite, new Version(3, minor));

    private static SqlSchemaSnapshot Snapshot(string version = "live-v1") =>
        new(SqlAgentToolType.Sqlite, version, new[]
        {
            SqlSchemaTable.WithTypes("dest", new[]
            {
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("label", SqlSchemaValueKind.Numeric)
            }),
            SqlSchemaTable.WithTypes("lookup", new[]
            {
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("payload", SqlSchemaValueKind.Numeric)
            })
        });

    private static SqliteConnection Open()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Execute(db, "CREATE TABLE dest(id INTEGER PRIMARY KEY,label INTEGER);" +
            "CREATE TABLE lookup(id INTEGER PRIMARY KEY,payload INTEGER);" +
            "CREATE TABLE bag(id INTEGER,payload INTEGER);" +
            "INSERT INTO dest(id,label) VALUES(1,0),(2,0),(3,0);" +
            "INSERT INTO lookup(id,payload) VALUES(2,7),(3,8);" +
            "INSERT INTO bag(id,payload) VALUES(2,6),(2,7);");
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqlPlanValidationContext Context(SqlSchemaSnapshot snapshot) =>
        new SqlPlanValidationContext("sqlite-live-key-policy",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dest", "lookup" })
            .WithSchemaSnapshot(snapshot);

    private static CompiledSqlCommand Compile(string sql, SqlPlanValidationContext context,
        int sourceMinor = 35, int targetMinor = 35) =>
        SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            context, new DmlCompilationPolicy(), Profile(sourceMinor),
            Profile(targetMinor), new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

    [Fact]
    public void ActualSqliteIntegerPrimaryKeysCanEnableBoundedCteUpdateFrom()
    {
        using var db = Open();
        var schema = Snapshot();
        var assurance = SqliteUpdateFromKeyAssurance.VerifyLive(db, schema,
            "dest", "id", "lookup", "id");
        var context = Context(schema).WithSqliteUpdateFromKeyAssurance(assurance);
        var command = Compile(Sql, context);
        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 2));
        Assert.NotEmpty(command.PlanFingerprint);

        using var execute = db.CreateCommand();
        execute.CommandText = command.Sql;
        foreach (var binding in command.Parameters)
        {
            var p = execute.CreateParameter();
            p.ParameterName = binding.Name;
            p.Value = binding.Value ?? DBNull.Value;
            execute.Parameters.Add(p);
        }
        using var reader = execute.ExecuteReader();
        var returned = new List<string>();
        while (reader.Read()) returned.Add(reader.GetInt32(0) + ":" + reader.GetInt32(1));
        Assert.Equal(new[] { "2:7", "3:8" }, returned.OrderBy(x => x));

        using var verify = db.CreateCommand();
        verify.CommandText = "SELECT id,label FROM dest ORDER BY id";
        using var all = verify.ExecuteReader();
        var rows = new List<string>();
        while (all.Read()) rows.Add(all.GetInt32(0) + ":" + all.GetInt32(1));
        Assert.Equal(new[] { "1:0", "2:7", "3:8" }, rows);
    }

    [Fact]
    public void StaticCatalogOrSchemaSnapshotWithoutLiveAssuranceCannotBypassPhysicalSourceFence()
    {
        using var db = Open();
        var schema = Snapshot();
        Assert.Throws<SqlCompilationException>(() => Compile(Sql, Context(schema)));
        Assert.Throws<SqlCompilationException>(() =>
            Compile(Sql, new SqlPlanValidationContext("no-schema")));
    }

    [Fact]
    public void SourceMustHaveRealSingleColumnIntegerPrimaryKey()
    {
        using var db = Open();
        var schema = Snapshot();
        Assert.ThrowsAny<ArgumentException>(() =>
            SqliteUpdateFromKeyAssurance.VerifyLive(db, schema, "dest", "id", "bag", "id"));
        Assert.ThrowsAny<ArgumentException>(() =>
            SqliteUpdateFromKeyAssurance.VerifyLive(db, schema, "dest", "label", "lookup", "id"));
        Assert.ThrowsAny<ArgumentException>(() =>
            SqliteUpdateFromKeyAssurance.VerifyLive(db, schema, "dest", "id", "lookup", "payload"));
    }

    [Theory]
    [InlineData("WITH checked AS (SELECT 1) UPDATE dest SET label = s.payload FROM lookup s WHERE dest.id = s.payload")]
    [InlineData("WITH checked AS (SELECT 1) UPDATE dest SET label = s.payload FROM lookup s WHERE dest.id = s.id OR dest.id = 1")]
    [InlineData("WITH checked AS (SELECT 1) UPDATE dest SET label = s.payload FROM lookup s WHERE dest.id = s.id")]
    [InlineData("WITH checked AS (SELECT 1) UPDATE dest SET label = b.payload FROM bag b WHERE dest.id = b.id")]
    public void WrongSourceOrUnprovenWhereConditionCannotBorrowKeyEvidence(string sql)
    {
        using var db = Open();
        var schema = Snapshot();
        var context = Context(schema).WithSqliteUpdateFromKeyAssurance(
            SqliteUpdateFromKeyAssurance.VerifyLive(db, schema, "dest", "id", "lookup", "id"));
        if (sql.EndsWith("WHERE dest.id = s.id", StringComparison.Ordinal))
        {
            Assert.Equal(SqlStatementKind.Update, Compile(sql, context).Kind);
        }
        else
        {
            Assert.Throws<SqlCompilationException>(() => Compile(sql, context));
        }
    }

    [Fact]
    public void ChangingSnapshotOrProviderVersionInvalidatesAssurance()
    {
        using var db = Open();
        var schema = Snapshot();
        var proof = SqliteUpdateFromKeyAssurance.VerifyLive(db, schema,
            "dest", "id", "lookup", "id");
        var changed = Context(Snapshot("another-schema"))
            .WithSqliteUpdateFromKeyAssurance(proof);
        Assert.Throws<SqlCompilationException>(() => Compile(Sql, changed));
        var current = Context(schema).WithSqliteUpdateFromKeyAssurance(proof);
        Assert.ThrowsAny<Exception>(() => Compile(Sql, current, targetMinor: 32));
    }

    [Fact]
    public void MetadataEvidenceIsBoundToFingerprintAndCannotBeReusedAsGenericApproval()
    {
        using var db = Open();
        var schema = Snapshot();
        var proof = SqliteUpdateFromKeyAssurance.VerifyLive(db, schema,
            "dest", "id", "lookup", "id");
        var context = Context(schema).WithSqliteUpdateFromKeyAssurance(proof);
        var command = Compile(Sql, context);
        Assert.NotEmpty(command.PlanFingerprint);
        Assert.NotNull(command.CompileEvidence);
        Assert.Equal(command.PlanFingerprint, command.CompileEvidence!.PlanFingerprint);
    }
}
