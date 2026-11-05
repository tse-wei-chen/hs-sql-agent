using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G10/Q02: native LAG/LEAD IGNORE/RESPECT NULLS. These modifiers
/// appear before OVER; they must never silently turn into ordinary LAG/LEAD.
/// </summary>
public sealed class SqlCoreWindowNullTreatmentTests
{
    internal static readonly (int Id, int? Lag, int? Lead)[] IgnoreCases =
    [
        (1, null, 20), (2, 10, 20), (3, 10, 20),
        (4, 10, 30), (5, 20, 40), (6, 30, 40),
        (7, 30, null), (8, null, 50), (9, null, 60),
        (10, 50, 60), (11, 50, null)
    ];

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, 16, 0, "IGNORE NULLS")]
    [InlineData(SqlAgentToolType.MsSqlServer, 16, 0, "RESPECT NULLS")]
    [InlineData(SqlAgentToolType.Oracle, 26, 0, "IGNORE NULLS")]
    [InlineData(SqlAgentToolType.Oracle, 26, 0, "RESPECT NULLS")]
    public void NativeNullTreatment_PreservesPlacementAndFunction(
        SqlAgentToolType provider, int major, int minor, string treatment)
    {
        var profile = Profile(provider, major, minor);
        foreach (var fn in new[] { "LAG", "LEAD" })
        {
            var command = Compile(Query("orders", fn, treatment), provider, provider, profile, profile);
            Assert.Contains(fn + "(", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(") " + treatment + " OVER (", command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenSource_RejectsIgnoreNullsAtParsing(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Query("orders", "LAG", "IGNORE NULLS"), source));
        Assert.Contains("window.null_treatment", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenTarget_RejectsIgnoreNullsBeforeRender(SqlAgentToolType target)
    {
        var sqlServer = Profile(SqlAgentToolType.MsSqlServer, 16, 0);
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(Query("orders", "LEAD", "IGNORE NULLS"),
                SqlAgentToolType.MsSqlServer, target, sqlServer, null));
        Assert.Contains("window.null_treatment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, 15, 0)]
    [InlineData(SqlAgentToolType.Oracle, 11, 1)]
    public void OldDeclaredVersion_MustRejectNullTreatment(
        SqlAgentToolType provider, int major, int minor)
    {
        var old = Profile(provider, major, minor);
        var current = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 26, 0);
        var sql = Query("orders", "LAG", "IGNORE NULLS");
        var sourceError = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, provider, provider, old, current));
        Assert.Contains("window.null_treatment", sourceError.Message, StringComparison.OrdinalIgnoreCase);
        var targetError = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, provider, provider, current, old));
        Assert.Contains("window.null_treatment", targetError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void MissingProfile_FailsClosedRatherThanAssumingNewServer(SqlAgentToolType provider)
    {
        var current = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 26, 0);
        var sql = Query("orders", "LAG", "IGNORE NULLS");
        var sourceError = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, provider, provider, null, current));
        Assert.Contains("window.null_treatment", sourceError.Message, StringComparison.OrdinalIgnoreCase);
        var targetError = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, provider, provider, current, null));
        Assert.Contains("window.null_treatment", targetError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(16, 0, 1000, 6)]
    [InlineData(16, 0, 4035, 3)]
    public void SqlServerBeforeCu4_CannotProveIgnoreNullsOnEitherSide(
        int major, int minor, int build, int revision)
    {
        // Microsoft documents a LAG/LEAD IGNORE NULLS correctness fix in SQL Server 2022 CU4.
        // A 16.0-only declaration must not be silently interpreted as CU4 or newer.
        var stale = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer, new Version(major, minor, build, revision));
        var patched = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer, new Version(16, 0, 4035, 4));
        var sql = Query("orders", "LAG", "IGNORE NULLS");
        var sourceError = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                stale, patched));
        Assert.Contains("CU4", sourceError.Message, StringComparison.OrdinalIgnoreCase);
        var targetError = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                patched, stale));
        Assert.Contains("CU4", targetError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerMajorMinorOnly_IsNotProofOfIgnoreNullsCu4()
    {
        var unspecifiedBuild = Profile(SqlAgentToolType.MsSqlServer, 16, 0);
        var patched = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer, new Version(16, 0, 4035, 4));
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(Query("orders", "LEAD", "IGNORE NULLS"),
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                unspecifiedBuild, patched));
        Assert.Contains("CU4", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "ignore nulls")]
    [InlineData(SqlAgentToolType.MsSqlServer, "IgNoRe NuLlS")]
    [InlineData(SqlAgentToolType.MsSqlServer, "respect nulls")]
    [InlineData(SqlAgentToolType.Oracle, "ignore nulls")]
    [InlineData(SqlAgentToolType.Oracle, "IgNoRe NuLlS")]
    [InlineData(SqlAgentToolType.Oracle, "respect nulls")]
    public void SoftKeywordCaseDoesNotAlterNativeNullTreatment(
        SqlAgentToolType provider, string modifier)
    {
        var profile = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 26, 0);
        var result = Compile(Query("orders", "LAG", modifier),
            provider, provider, profile, profile);
        Assert.Contains(" " + modifier.ToUpperInvariant() + " OVER (",
            result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void StructuredWindowDefinition_UsesIdenticalTypedProof(
        SqlAgentToolType provider)
    {
        var profile = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 26, 0);
        var query = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "LAG",
                    Arguments = [new FieldSelectCondition { FieldName = "amount" }],
                    Window = new WindowDefinition
                    {
                        OrderBy =
                        [
                            new FieldOrderByCondition
                            {
                                FieldName = "id", Direction = SortDirection.Asc
                            }
                        ],
                        NullTreatment = WindowNullTreatmentMode.IgnoreNulls
                    }
                }
            ]
        };
        var ast = QueryDefinitionCoreMapper.Map(query);
        var parsed = new ParsedStatement(ast, provider, false, profile);
        var compiled = CoreSqlCompiler.CreateDefault().Compile(
            parsed, provider,
            new SqlPlanValidationContext("g10-window-null-treatment-dto"),
            new SqlExecutionPlanPolicy(), profile);
        Assert.Contains("IGNORE NULLS OVER (",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicLegacyWindowSpecConstructor_RemainsBinaryCompatible()
    {
        var window = new HsSqlAgent.SqlCore.Core.Ast.WindowSpec(
            System.Collections.Immutable.ImmutableArray<HsSqlAgent.SqlCore.Core.Ast.SqlExpr>.Empty,
            System.Collections.Immutable.ImmutableArray<HsSqlAgent.SqlCore.Core.Ast.OrderByItem>.Empty,
            null,
            HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown);
        Assert.Equal(HsSqlAgent.SqlCore.Core.Ast.WindowNullTreatmentKind.Unspecified,
            window.NullTreatment);
        window.NullTreatment = HsSqlAgent.SqlCore.Core.Ast.WindowNullTreatmentKind.IgnoreNulls;
        Assert.Equal(HsSqlAgent.SqlCore.Core.Ast.WindowNullTreatmentKind.IgnoreNulls,
            window.NullTreatment);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "ignore")]
    [InlineData(SqlAgentToolType.Postgres, "respect")]
    [InlineData(SqlAgentToolType.MsSqlServer, "ignore")]
    [InlineData(SqlAgentToolType.MsSqlServer, "respect")]
    public void OrdinaryFunctionAliasesAreNotNullTreatment(
        SqlAgentToolType provider, string alias)
    {
        var profile = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 16, 0);
        var command = Compile("SELECT ABS(amount) " + alias + " FROM orders",
            provider, provider, profile, profile);
        Assert.DoesNotContain("IGNORE NULLS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RESPECT NULLS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(alias, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("COUNT")]
    [InlineData("SUM")]
    [InlineData("ROW_NUMBER")]
    public void NonLagLeadFunctions_RejectNullTreatment(string fn)
    {
        var profile = Profile(SqlAgentToolType.MsSqlServer, 16, 0);
        var arg = fn == "ROW_NUMBER" ? "" : "value";
        var sql = "SELECT " + fn + "(" + arg + ") IGNORE NULLS OVER (ORDER BY id) FROM orders";
        Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MsSqlServer, profile));
    }

    [Theory]
    [InlineData("LAG(value) IGNORE NULLS")]
    [InlineData("LAG(value) IGNORE NULLS IGNORE NULLS OVER (ORDER BY id)")]
    [InlineData("LAG(value) IGNORE OVER (ORDER BY id)")]
    [InlineData("LAG(value) NULLS OVER (ORDER BY id)")]
    [InlineData("LEAD(value) RESPECT NULLS IGNORE NULLS OVER (ORDER BY id)")]
    public void MalformedNullTreatmentGrammar_FailsClosed(string clause)
    {
        var profile = Profile(SqlAgentToolType.MsSqlServer, 16, 0);
        Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery("SELECT " + clause + " FROM orders",
                SqlAgentToolType.MsSqlServer, profile));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void PlainLagLeadWithoutExplicitModifier_RemainsUnchanged(SqlAgentToolType provider)
    {
        var profile = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 26, 0);
        var command = Compile(Query("orders", "LAG", ""), provider, provider, profile, profile);
        Assert.DoesNotContain("IGNORE NULLS", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RESPECT NULLS", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void CapabilityMatrix_OnlyModelsProvenNativeProfiles(SqlAgentToolType provider)
    {
        var old = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 15 : 11, 0);
        var current = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 26, 0);
        Assert.Equal(SqlCapabilityStatus.Rejected, Status(SqlCapabilityMatrix.ForProvider(provider, old)));
        Assert.Equal(SqlCapabilityStatus.Supported, Status(SqlCapabilityMatrix.ForProvider(provider, current)));
        Assert.Equal(SqlCapabilityStatus.Rejected, Status(SqlCapabilityMatrix.ForProvider(provider)));
    }

    private static SqlCapabilityStatus Status(ProviderSqlCapabilities capabilities) =>
        Assert.Single(capabilities.Capabilities, item => item.Id == "window.null_treatment").Status;

    internal static string Query(string table, string fn, string modifier) =>
        "SELECT id, " + fn + "(value)" + (modifier.Length == 0 ? "" : " " + modifier) +
        " OVER (PARTITION BY grp ORDER BY id) AS picked FROM " + table + " ORDER BY id";

    internal static SqlProviderCapabilityProfile Profile(SqlAgentToolType provider, int major, int minor) =>
        new(provider, new Version(major, minor));

    internal static CompiledSqlCommand Compile(string sql, SqlAgentToolType source,
        SqlAgentToolType target, SqlProviderCapabilityProfile? sourceProfile,
        SqlProviderCapabilityProfile? targetProfile) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source, sourceProfile),
            target,
            new SqlPlanValidationContext("g10-lag-lead-nulls-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    internal static async Task Run(DbConnection db, string sql, CancellationToken token)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    internal static async Task<int?[]> Read(DbConnection db, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var binding in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = binding.Name;
            p.Value = binding.Value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        var result = new List<int?>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1)));
        return result.ToArray();
    }

    internal static async Task VerifyNativeAndCompiled(DbConnection db, string table,
        SqlAgentToolType provider, CancellationToken token)
    {
        var profile = Profile(provider, provider == SqlAgentToolType.MsSqlServer ? 16 : 26, 0);
        foreach (var fn in new[] { "LAG", "LEAD" })
        {
            var sql = Query(table, fn, "IGNORE NULLS");
            var compiled = Compile(sql, provider, provider, profile, profile);
            var actual = await Read(db, compiled.Sql, compiled.Parameters, token);
            var native = await Read(db, sql, [], token);
            var expected = IgnoreCases.Select(x => fn == "LAG" ? x.Lag : x.Lead).ToArray();
            Assert.Equal(expected, actual);
            Assert.Equal(native, actual);
        }
    }

    internal static async Task Seed(DbConnection db, string table, CancellationToken token)
    {
        await Run(db, "INSERT INTO " + table +
            " (id,grp,value) VALUES (1,1,10),(2,1,NULL),(3,1,NULL)," +
            "(4,1,20),(5,1,30),(6,1,NULL),(7,1,40)," +
            "(8,2,NULL),(9,2,50),(10,2,NULL),(11,2,60)", token);
    }
}

public sealed class SqlCoreSqlServerWindowNullTreatmentLiveTests
{
    [Fact]
    public async Task SqlServer2022_IgnoreNullsMatchesActualNativeRows()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true") throw new InvalidOperationException("TEST_SQLSERVER_CONNECTION required.");
            Assert.Skip("Requires live SQL Server");
            return;
        }
        var token = TestContext.Current.CancellationToken;
        var table = "SQLCORE_WN_" + Guid.NewGuid().ToString("N")[..12];
        await using DbConnection db = new SqlConnection(cs);
        await db.OpenAsync(token);
        await SqlCoreWindowNullTreatmentTests.Run(db,
            "CREATE TABLE " + table + " (id INT, grp INT, value INT NULL)", token);
        try
        {
            await SqlCoreWindowNullTreatmentTests.Seed(db, table, token);
            await SqlCoreWindowNullTreatmentTests.VerifyNativeAndCompiled(
                db, table, SqlAgentToolType.MsSqlServer, token);
        }
        finally
        {
            await SqlCoreWindowNullTreatmentTests.Run(db, "DROP TABLE " + table, token);
        }
    }
}

public sealed class SqlCoreOracleWindowNullTreatmentLiveTests
{
    [Fact]
    public async Task Oracle26_IgnoreNullsMatchesActualNativeRows()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true") throw new InvalidOperationException("TEST_ORACLE_CONNECTION required.");
            Assert.Skip("Requires live Oracle");
            return;
        }
        var token = TestContext.Current.CancellationToken;
        var table = "SQLCORE_WN_" + Guid.NewGuid().ToString("N")[..12];
        await using DbConnection db = new OracleConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (OracleException) when (retry < 60 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        await SqlCoreWindowNullTreatmentTests.Run(db,
            "CREATE TABLE " + table + " (id NUMBER(10), grp NUMBER(10), value NUMBER(10))", token);
        try
        {
            await SqlCoreWindowNullTreatmentTests.Seed(db, table, token);
            await SqlCoreWindowNullTreatmentTests.VerifyNativeAndCompiled(
                db, table, SqlAgentToolType.Oracle, token);
        }
        finally
        {
            await SqlCoreWindowNullTreatmentTests.Run(db, "DROP TABLE " + table + " PURGE", token);
        }
    }
}
