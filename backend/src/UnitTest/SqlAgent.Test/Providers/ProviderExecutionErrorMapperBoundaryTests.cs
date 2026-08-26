using Xunit;

namespace SqlAgent.Test.Providers;

public sealed class ProviderExecutionErrorMapperBoundaryTests
{
    [Fact]
    public void PostgresProperties_AreReadWithoutDriverReference()
    {
        var error = Map(
            SqlAgentToolType.Postgres,
            new FakePostgresException("raw", "23505", "duplicate key"));

        Assert.Equal("23505", error.Code);
        Assert.Equal("duplicate key", error.ProviderMessage);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, 1062, "1062")]
    [InlineData(SqlAgentToolType.MsSqlServer, 2627, "2627")]
    public void NumericProviderCodes_AreReadWithoutDriverReference(
        SqlAgentToolType provider,
        int number,
        string expected)
    {
        var error = Map(provider, new FakeNumberException("provider failure", number));

        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public void SqliteCode_IsNormalizedWithoutDriverReference()
    {
        var error = Map(
            SqlAgentToolType.Sqlite,
            new FakeSqliteException("constraint failed", 19));

        Assert.Equal("SQLITE_19", error.Code);
    }

    [Fact]
    public void FirebirdErrorCode_FallsBackToPublicProperty()
    {
        var error = Map(
            SqlAgentToolType.Firebird,
            new FakeFirebirdException("provider failure", 335544665));

        Assert.Equal("335544665", error.Code);
    }

    [Fact]
    public void OracleCode_KeepsStableMessageFallback()
    {
        var error = Map(
            SqlAgentToolType.Oracle,
            new Exception("ORA-00001: unique constraint violated"));

        Assert.Equal("ORA-00001", error.Code);
    }

    private static ProviderExecutionException Map(
        SqlAgentToolType provider,
        Exception exception) =>
        Assert.IsType<ProviderExecutionException>(
            new ProviderExecutionErrorMapper(provider).Map(exception, "query"));

    private sealed class FakePostgresException(
        string message,
        string sqlState,
        string messageText) : Exception(message)
    {
        public string SqlState { get; } = sqlState;
        public string MessageText { get; } = messageText;
    }

    private sealed class FakeNumberException(string message, int number) : Exception(message)
    {
        public int Number { get; } = number;
    }

    private sealed class FakeSqliteException(string message, int sqliteErrorCode) : Exception(message)
    {
        public int SqliteErrorCode { get; } = sqliteErrorCode;
    }

    private sealed class FakeFirebirdException(string message, int errorCode) : Exception(message)
    {
        public int ErrorCode { get; } = errorCode;
    }
}
