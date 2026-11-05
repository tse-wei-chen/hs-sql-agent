using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreFirebirdOffsetTimestampCapabilityTests
{
    private static readonly SqlOffsetDateTimeValue OffsetValue = new(
        new DateTimeOffset(2026, 8, 27, 17, 30, 0, TimeSpan.FromHours(8)));

    [Fact]
    public void Matrix_MySqlOffsetTimestamp_RemainsRejected()
    {
        var capability = OffsetCapability(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MySQL));

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains(
            "UTC offset",
            capability.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileQuery_MySqlOffsetTimestamp_UsesCanonicalCapabilityId()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                OffsetQuery(),
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("mysql-offset-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(
            "temporal.offset_timestamp",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "literal.timestamp_offset",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_FirebirdOffsetTimestamp_RequiresVersion4Profile()
    {
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            OffsetCapability(SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Firebird)).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            OffsetCapability(SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Firebird,
                FirebirdProfile(3))).Status);

        var supported = OffsetCapability(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Firebird,
            FirebirdProfile(4)));
        Assert.Equal(SqlCapabilityStatus.Translated, supported.Status);
        Assert.Contains("4.0+", supported.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void CompileQuery_FirebirdOffsetTimestamp_FailsClosedWithoutVersion4(
        int? majorVersion)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                OffsetQuery(),
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("firebird-offset-v1"),
                new SqlExecutionPlanPolicy(),
                majorVersion is null ? null : FirebirdProfile(majorVersion.Value)));

        Assert.Contains("temporal.offset_timestamp", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileQuery_Firebird4OffsetTimestamp_UsesTimestampWithTimeZone()
    {
        var command = CoreSqlCompiler.CreateDefault().Compile(
            OffsetQuery(),
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-offset-v1"),
            new SqlExecutionPlanPolicy(),
            FirebirdProfile(4));

        Assert.Contains("TIMESTAMP WITH TIME ZONE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void CompileInsert_FirebirdOffsetTimestamp_FailsClosedWithoutVersion4(
        int? majorVersion)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                OffsetInsert(),
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("firebird-offset-v1"),
                new DmlCompilationPolicy(),
                majorVersion is null ? null : FirebirdProfile(majorVersion.Value)));

        Assert.Contains("temporal.offset_timestamp", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileInsert_Firebird4OffsetTimestamp_UsesTimestampWithTimeZone()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            OffsetInsert(),
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-offset-v1"),
            new DmlCompilationPolicy(),
            FirebirdProfile(4));

        Assert.Contains("TIMESTAMP WITH TIME ZONE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
    }

    private static SqlCapability OffsetCapability(ProviderSqlCapabilities matrix) =>
        Assert.Single(matrix.Capabilities, item => item.Id == "temporal.offset_timestamp");

    private static ParsedStatement OffsetQuery()
    {
        var definition = new QueryDefinition
        {
            TableName = "events",
            SelectColumns =
            [
                new ConstantSelectCondition
                {
                    Constant = OffsetValue,
                    Alias = "event_time"
                }
            ]
        };
        return new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);
    }

    private static ParsedStatement OffsetInsert()
    {
        var span = SourceSpan.Unknown;
        var insert = new InsertStatement(
            new NamedTableSource(
                SqlIdentifier.Unquoted("events", span),
                null,
                span),
            [SqlIdentifier.Unquoted("event_time", span)],
            new InsertValuesSource(
                [
                    [new LiteralExpr(OffsetValue, span)]
                ],
                span),
            span);
        return new ParsedStatement(insert, SqlAgentToolType.Postgres);
    }

    private static SqlProviderCapabilityProfile FirebirdProfile(int majorVersion) =>
        new(
            SqlAgentToolType.Firebird,
            ServerVersion: new Version(majorVersion, 0));
}
