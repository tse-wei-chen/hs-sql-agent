using System.Data.Common;
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlTargetUniqueKeyErrorsTests
{
    private sealed class GenericDatabaseFailure(int code, string? sqlState = null)
        : DbException("Injected non-unique database error", code)
    {
        public override string? SqlState => sqlState;
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnknownErrorProvider_MustNeverPassReadiness(SqlAgentToolType dialect)
    {
        // A generic DbException with borrowed vendor code / SQLSTATE is not
        // proof of a physical unique index from the intended ADO provider.
        Assert.False(DmlTargetUniqueKeyErrors.IsUniqueViolation(
            dialect, new GenericDatabaseFailure(335544665, "23505")));
        Assert.False(DmlTargetUniqueKeyErrors.IsUniqueViolation(
            dialect, new GenericDatabaseFailure(1062, "23000")));
        Assert.False(DmlTargetUniqueKeyErrors.IsUniqueViolation(
            dialect, new GenericDatabaseFailure(1, "23505")));
        Assert.False(DmlTargetUniqueKeyErrors.IsUniqueViolation(
            dialect, new GenericDatabaseFailure(2601)));
    }
}
