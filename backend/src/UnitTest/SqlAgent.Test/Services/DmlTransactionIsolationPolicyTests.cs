using System.Data;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlTransactionIsolationPolicyTests
{
    private readonly StrictDmlTransactionIsolationPolicy _policy = new();

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public void PreviewIsolation_UsesRepeatableReadForSnapshotProviders(SqlAgentToolType provider)
    {
        Assert.Equal(IsolationLevel.RepeatableRead, _policy.PreviewIsolation(provider));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void PreviewIsolation_UsesSerializableWhenPortableSnapshotModeIsUnavailable(SqlAgentToolType provider)
    {
        Assert.Equal(IsolationLevel.Serializable, _policy.PreviewIsolation(provider));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CommitIsolation_IsSerializableForEverySupportedProvider(SqlAgentToolType provider)
    {
        Assert.Equal(IsolationLevel.Serializable, _policy.CommitIsolation(provider));
    }

    [Fact]
    public void IsolationPolicy_UnknownProvider_FailsClosed()
    {
        var unknown = (SqlAgentToolType)int.MaxValue;
        Assert.Throws<ArgumentOutOfRangeException>(() => _policy.PreviewIsolation(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => _policy.CommitIsolation(unknown));
    }
}
