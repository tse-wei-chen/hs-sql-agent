using System.Data;
using System.Reflection;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlPreviewTransactionFactoryTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "BeforeTransactionSql")]
    [InlineData(SqlAgentToolType.Postgres, "InTransactionSql")]
    [InlineData(SqlAgentToolType.Oracle, "InTransactionSql")]
    [InlineData(SqlAgentToolType.Firebird, "NativeTransactionOptions")]
    [InlineData(SqlAgentToolType.MsSqlServer, "NotAvailable")]
    [InlineData(SqlAgentToolType.Sqlite, "NotAvailable")]
    public void ReadOnlyMode_IsExplicitPerProvider(
        SqlAgentToolType provider,
        string expected)
    {
        var method = typeof(ProviderDmlPreviewTransactionFactory).GetMethod(
            "ReadOnlyMode",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [provider]);
        Assert.Equal(expected, result?.ToString());
    }

    [Theory]
    [InlineData(IsolationLevel.Serializable, FbTransactionBehavior.Consistency)]
    [InlineData(IsolationLevel.RepeatableRead, FbTransactionBehavior.Concurrency)]
    [InlineData(IsolationLevel.Snapshot, FbTransactionBehavior.Concurrency)]
    [InlineData(IsolationLevel.ReadCommitted, FbTransactionBehavior.ReadCommitted)]
    public void FirebirdBehavior_AlwaysIncludesReadOnlyAndExpectedIsolation(
        IsolationLevel isolation,
        FbTransactionBehavior expectedIsolationFlag)
    {
        var method = typeof(FirebirdDmlPreviewTransactionFactory).GetMethod(
            "ResolveBehavior",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var behavior = Assert.IsType<FbTransactionBehavior>(method!.Invoke(null, [isolation]));

        Assert.True(behavior.HasFlag(FbTransactionBehavior.Read));
        Assert.False(behavior.HasFlag(FbTransactionBehavior.Write));
        Assert.True(behavior.HasFlag(FbTransactionBehavior.NoWait));
        Assert.True(behavior.HasFlag(expectedIsolationFlag));
    }

    [Fact]
    public void FirebirdProvider_UsesProviderOwnedNativePreviewFactory()
    {
        var provider = new FirebirdProvider();

        Assert.IsType<FirebirdDmlPreviewTransactionFactory>(provider.PreviewTransactions);
        Assert.Equal("HsSqlAgent.Provider.Firebird", provider.PreviewTransactions.GetType().Assembly.GetName().Name);
    }

    [Fact]
    public void SetupSql_IsFixedReadOnlyTransactionStatement()
    {
        var field = typeof(ProviderDmlPreviewTransactionFactory).GetField(
            "ReadOnlyTransactionSql",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal("SET TRANSACTION READ ONLY", field!.GetRawConstantValue());
    }

    [Fact]
    public async Task Sqlite_UsesPortableTransactionWithoutPretendingNativeReadOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var factory = new ProviderDmlPreviewTransactionFactory();

        await using var transaction = await factory.BeginAsync(
            connection,
            SqlAgentToolType.Sqlite,
            IsolationLevel.Serializable,
            cancellationToken);

        Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);
        await transaction.RollbackAsync(cancellationToken);
    }

    [Fact]
    public async Task GenericFactory_RejectsFirebirdInsteadOfSilentlyDroppingNativeReadOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var factory = new ProviderDmlPreviewTransactionFactory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.BeginAsync(
                connection,
                SqlAgentToolType.Firebird,
                IsolationLevel.Serializable,
                TestContext.Current.CancellationToken));

        Assert.Contains("provider-native", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirebirdFactory_RejectsNonFirebirdConnectionInsteadOfSilentlyDroppingReadOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var factory = new FirebirdDmlPreviewTransactionFactory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.BeginAsync(
                connection,
                SqlAgentToolType.Firebird,
                IsolationLevel.Serializable,
                TestContext.Current.CancellationToken));

        Assert.Contains("FbConnection", error.Message, StringComparison.Ordinal);
    }
}
