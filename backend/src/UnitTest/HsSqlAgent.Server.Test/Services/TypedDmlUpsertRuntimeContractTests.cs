using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class TypedDmlUpsertRuntimeContractTests
{
    [Fact]
    public void UpsertConflictStatement_RemainsOutsideImmutableInsertApprovalMode()
    {
        var statement = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres).Statement;

        Assert.False(TypedDmlRuntime.SupportsStatement(statement));
        var error = Assert.Throws<NotSupportedException>(() =>
            TypedDmlRuntime.EnsureSupportedStatement(statement));

        Assert.Contains("upsert", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing-row", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("previewed", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
