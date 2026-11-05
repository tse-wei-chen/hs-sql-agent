using HsSqlAgent.SqlCore;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G10/Q02 EXCLUDE is frame membership, never a predicate or a ROWS/RANGE rewrite.
/// Verify all four explicit forms and peer-sensitive SQL against two real engines.
/// </summary>
public sealed class SqlCoreWindowExcludeFrameTests
{
    private const string Table = "sqlcore_exclude_frames";
    private const string Rows = "ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING";
    private const string Groups = "GROUPS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";
    private const string Range = "RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";

    public static IEnumerable<object[]> NativeCases()
    {
        foreach (var provider in new[] { SqlAgentToolType.Postgres, SqlAgentToolType.Sqlite })
        foreach (var unit in new[] { Rows, Groups, Range })
        foreach (var excluded in new[] { "CURRENT ROW", "GROUP", "TIES", "NO OTHERS" })
            yield return [provider, unit, excluded];
    }

    private static string Query(string frame, string exclusion) =>
        $"SELECT id, SUM(amount) OVER (ORDER BY grp {frame} EXCLUDE {exclusion}) AS total " +
        $"FROM {Table} ORDER BY id";

    [Theory]
    [MemberData(nameof(NativeCases))]
    public void NativeParserAndRenderer_PreserveExactExclusion(
        SqlAgentToolType provider, string frame, string exclusion)
    {
        var command = Compile(Query(frame, exclusion), provider, provider);
        Assert.Contains($"{frame} EXCLUDE {exclusion}", command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenSourceRejectsExplicitExclusionAtParse(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Query(Rows, "NO OTHERS"), source));
        Assert.Contains("window.exclude", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenTargetRejectsExplicitExclusionBeforeRendering(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(Query(Rows, "TIES"), SqlAgentToolType.Postgres, target));
        Assert.Contains("window.exclude", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CURRENT")]
    [InlineData("NO")]
    [InlineData("SOMETHING")]
    [InlineData("CURRENT ROW EXCLUDE GROUP")]
    [InlineData("TIES EXCLUDE TIES")]
    public void InvalidExclusionGrammarFailsClosed(string exclusion)
    {
        Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Query(Rows, exclusion), SqlAgentToolType.Postgres));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, WindowFrameExclusionMode.CurrentRow, "EXCLUDE CURRENT ROW")]
    [InlineData(SqlAgentToolType.Sqlite, WindowFrameExclusionMode.Group, "EXCLUDE GROUP")]
    [InlineData(SqlAgentToolType.Postgres, WindowFrameExclusionMode.Ties, "EXCLUDE TIES")]
    [InlineData(SqlAgentToolType.Sqlite, WindowFrameExclusionMode.NoOthers, "EXCLUDE NO OTHERS")]
    public void StructuredQueryDefinition_ExclusionRoundTripsIntoNativeCompiler(
        SqlAgentToolType provider, WindowFrameExclusionMode mode, string sqlFragment)
    {
        var definition = new QueryDefinition
        {
            TableName = Table,
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "SUM",
                    Arguments = [new FieldSelectCondition { FieldName = "amount" }],
                    Window = new WindowDefinition
                    {
                        OrderBy = [new FieldOrderByCondition { FieldName = "grp", Direction = SortDirection.Asc }],
                        Frame = new WindowFrameDefinition
                        {
                            Unit = WindowFrameUnit.Groups,
                            Start = new WindowFrameBound { Kind = WindowFrameBoundKind.UnboundedPreceding },
                            End = new WindowFrameBound { Kind = WindowFrameBoundKind.CurrentRow },
                            Exclusion = mode
                        }
                    }
                }
            ]
        };
        var compiled = CoreSqlCompiler.CreateDefault().Compile(
            new ParsedStatement(QueryDefinitionCoreMapper.Map(definition), provider),
            provider,
            new SqlPlanValidationContext("g10-structured-exclude-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("GROUPS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW", compiled.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sqlFragment, compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyFrameCtorStillMeansImplicitDefaultAndClrEnumsKeepOldValues()
    {
        Assert.Equal(0, (int)WindowFrameExclusionMode.Unspecified);
        Assert.Equal(0, (int)WindowFrameUnit.Rows);
        Assert.Equal(1, (int)WindowFrameUnit.Range);
        Assert.Equal(2, (int)WindowFrameUnit.Groups);
        var frame = new HsSqlAgent.SqlCore.Core.Ast.WindowFrame(
            HsSqlAgent.SqlCore.Core.Ast.WindowFrameUnitKind.Rows,
            new HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundCore(
                HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.CurrentRow,
                default, HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown),
            null,
            HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown);
        Assert.Equal(HsSqlAgent.SqlCore.Core.Ast.WindowFrameExclusionKind.Unspecified,
            frame.Exclusion);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, 10, 0, 11, 0)]
    [InlineData(SqlAgentToolType.Sqlite, 3, 27, 3, 28)]
    public void ExplicitlyOldSourceOrTargetVersionsRemainRejected(
        SqlAgentToolType provider, int oldMajor, int oldMinor, int newMajor, int newMinor)
    {
        var oldProfile = new SqlProviderCapabilityProfile(provider, new Version(oldMajor, oldMinor));
        var current = new SqlProviderCapabilityProfile(provider, new Version(newMajor, newMinor));
        Assert.Equal(SqlCapabilityStatus.Rejected,
            Assert.Single(SqlCapabilityMatrix.ForProvider(provider, oldProfile).Capabilities,
                capability => capability.Id == "window.exclude").Status);
        Assert.Equal(SqlCapabilityStatus.Supported,
            Assert.Single(SqlCapabilityMatrix.ForProvider(provider, current).Capabilities,
                capability => capability.Id == "window.exclude").Status);

        var policy = new SqlExecutionPlanPolicy();
        var context = new SqlPlanValidationContext("g10-window-exclude-version-v1");
        var sql = Query(Rows, "GROUP");
        var targetError = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(sql, provider, provider, context, policy, oldProfile));
        Assert.Contains("window.exclude", targetError.Message, StringComparison.OrdinalIgnoreCase);
        var sourceError = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(sql, provider, provider, context, policy, oldProfile, current));
        Assert.Contains("window.exclude", sourceError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public async Task RealDatabase_AllExclusionsPreservePeerSemantics(SqlAgentToolType provider)
    {
        var pg = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (provider == SqlAgentToolType.Postgres && string.IsNullOrWhiteSpace(pg))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("PostgreSQL live parity requires TEST_POSTGRES_CONNECTION in CI.");
            Assert.Skip("TEST_POSTGRES_CONNECTION is required for live PostgreSQL parity.");
            return;
        }

        await using DbConnection db = provider == SqlAgentToolType.Postgres
            ? new NpgsqlConnection(pg)
            : new SqliteConnection("Data Source=:memory:");
        var token = TestContext.Current.CancellationToken;
        await db.OpenAsync(token);
        await using (var setup = db.CreateCommand())
        {
            setup.CommandText =
                $"CREATE TEMP TABLE {Table} (id INTEGER, grp INTEGER, amount INTEGER);" +
                $"INSERT INTO {Table} (id, grp, amount) VALUES " +
                "(1,1,2),(2,1,3),(3,2,7),(4,3,11);";
            await setup.ExecuteNonQueryAsync(token);
        }

        foreach (var (frame, exclusion, expected) in new[]
        {
            (Groups, "NO OTHERS", new long?[] {5,5,12,23}),
            (Groups, "CURRENT ROW", new long?[] {3,2,5,12}),
            (Groups, "GROUP", new long?[] {null,null,5,12}),
            (Groups, "TIES", new long?[] {2,3,12,23}),
            (Range, "NO OTHERS", new long?[] {5,5,12,23}),
            (Range, "CURRENT ROW", new long?[] {3,2,5,12}),
            (Range, "GROUP", new long?[] {null,null,5,12}),
            (Range, "TIES", new long?[] {2,3,12,23}),
            (Rows, "NO OTHERS", new long?[] {23,23,23,23}),
            (Rows, "CURRENT ROW", new long?[] {21,20,16,12}),
            (Rows, "GROUP", new long?[] {18,18,16,12}),
            (Rows, "TIES", new long?[] {20,21,23,23})
        })
        {
            var compiled = Compile(Query(frame, exclusion), provider, provider);
            await using var command = db.CreateCommand();
            command.CommandText = compiled.Sql;
            foreach (var bound in compiled.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = bound.Name;
                parameter.Value = bound.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            var found = new List<long?>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                found.Add(reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1)));

            Assert.Equal(expected, found);
        }
    }

    private static CompiledSqlCommand Compile(
        string sql, SqlAgentToolType source, SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source), target,
            new SqlPlanValidationContext("g10-window-exclude-v1"),
            new SqlExecutionPlanPolicy());
}
