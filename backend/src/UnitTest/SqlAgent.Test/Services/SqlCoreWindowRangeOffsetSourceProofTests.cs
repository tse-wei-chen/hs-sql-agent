using System.Data.Common;
using HsSqlAgent.SqlCore;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G10/Q02: source-dialect RANGE offsets must not be accepted merely because
/// a different target database supports them. SQL Server allows numeric
/// PRECEDING/FOLLOWING offsets for ROWS, never for RANGE.
/// </summary>
public sealed class SqlCoreWindowRangeOffsetSourceProofTests
{
    private const string TableName = "sqlcore_range_source_proof";

    private static string Query(string frame) =>
        $"SELECT id, SUM(amount) OVER (ORDER BY grp {frame}) AS total FROM {TableName} ORDER BY id";

    [Theory]
    [InlineData("RANGE 1 PRECEDING")]
    [InlineData("RANGE 0 PRECEDING")]
    [InlineData("RANGE BETWEEN 1 PRECEDING AND CURRENT ROW")]
    [InlineData("RANGE BETWEEN UNBOUNDED PRECEDING AND 2 FOLLOWING")]
    [InlineData("RANGE BETWEEN 1 PRECEDING AND 2 FOLLOWING")]
    [InlineData("RANGE BETWEEN CURRENT ROW AND 0 FOLLOWING")]
    public void SqlServerSource_NumericRangeOffsetsAreRejectedAtSource(string frame)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Query(frame), SqlAgentToolType.MsSqlServer));
        Assert.Contains("window.range_offset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerSource_CannotAcquirePostgresExecutionByCrossProviderTranslation()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(
                Query("RANGE BETWEEN 1 PRECEDING AND CURRENT ROW"),
                SqlAgentToolType.MsSqlServer,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("g10-range-source-proof"),
                new SqlExecutionPlanPolicy()));
        Assert.Contains("window.range_offset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ROWS BETWEEN 1 PRECEDING AND CURRENT ROW")]
    [InlineData("ROWS BETWEEN CURRENT ROW AND 1 FOLLOWING")]
    [InlineData("RANGE UNBOUNDED PRECEDING")]
    [InlineData("RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW")]
    [InlineData("RANGE BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING")]
    public void SqlServerSource_ProvenRowsAndRangeShapesRemainAccepted(string frame)
    {
        var parsed = CoreSqlTextParser.ParseQuery(Query(frame), SqlAgentToolType.MsSqlServer);
        Assert.NotNull(parsed.Statement);
    }

    [Fact]
    public void StructuredSqlServerSource_CannotBypassRangeOffsetProof()
    {
        var definition = new QueryDefinition
        {
            TableName = TableName,
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
                            Unit = WindowFrameUnit.Range,
                            Start = new WindowFrameBound
                            {
                                Kind = WindowFrameBoundKind.Preceding,
                                Offset = 1
                            },
                            End = new WindowFrameBound { Kind = WindowFrameBoundKind.CurrentRow }
                        }
                    }
                }
            ]
        };

        var statement = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.MsSqlServer);
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                statement,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("g10-structured-range-source-proof"),
                new SqlExecutionPlanPolicy()));
        Assert.Contains("window.range_offset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void NativeNumericRange_SourceAndTargetRetainOffsetSemantics(SqlAgentToolType provider)
    {
        var compiled = Compile(provider);
        Assert.Contains("RANGE BETWEEN 1 PRECEDING AND CURRENT ROW",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ROWS BETWEEN", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public async Task NativeNumericRange_RealDatabaseMatchesOrderValueRange(SqlAgentToolType provider)
    {
        var postgres = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (provider == SqlAgentToolType.Postgres && string.IsNullOrWhiteSpace(postgres))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("PostgreSQL live parity requires TEST_POSTGRES_CONNECTION in CI.");
            Assert.Skip("TEST_POSTGRES_CONNECTION is required for PostgreSQL parity.");
            return;
        }

        await using DbConnection connection = provider == SqlAgentToolType.Postgres
            ? new NpgsqlConnection(postgres)
            : new SqliteConnection("Data Source=:memory:");
        var token = TestContext.Current.CancellationToken;
        await connection.OpenAsync(token);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                $"CREATE TEMP TABLE {TableName} (id INTEGER, grp INTEGER, amount INTEGER);" +
                $"INSERT INTO {TableName} (id, grp, amount) VALUES " +
                "(1,1,2),(2,1,3),(3,2,7),(4,3,11);";
            await setup.ExecuteNonQueryAsync(token);
        }

        var compiled = Compile(provider);
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        foreach (var bound in compiled.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = bound.Name;
            parameter.Value = bound.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var totals = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            totals.Add(Convert.ToInt64(reader.GetValue(1)));

        Assert.Equal(new long[] { 5, 5, 12, 18 }, totals);
    }

    private static CompiledSqlCommand Compile(SqlAgentToolType provider) =>
        SqlCoreFacade.CompileQuery(
            Query("RANGE BETWEEN 1 PRECEDING AND CURRENT ROW"),
            provider,
            provider,
            new SqlPlanValidationContext("g10-native-numeric-range"),
            new SqlExecutionPlanPolicy());
}
