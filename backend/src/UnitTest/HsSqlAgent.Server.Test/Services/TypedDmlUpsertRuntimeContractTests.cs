using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class TypedDmlUpsertRuntimeContractTests
{
    [Fact]
    public void BoundedPostgresUpsert_IsOnlyStructurallyAdmitted_NotInsertValuesApproval()
    {
        var statement = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " +
            "ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres).Statement;

        // This guard checks only the closed AST shape. The factory and
        // coordinator must still require the physically attested
        // InsertConflictDoUpdate mode, full before/after row evidence and
        // transaction-held schema lock, never plain InsertValues.
        Assert.True(TypedDmlRuntime.SupportsStatement(statement));
        TypedDmlRuntime.EnsureSupportedStatement(statement);
    }

    [Theory]
    [InlineData("ON CONFLICT (id) DO UPDATE SET id = excluded.id")]
    [InlineData("ON CONFLICT (id) DO UPDATE SET name = upper(excluded.name)")]
    [InlineData("ON CONFLICT (id) DO UPDATE SET name = 'forged'")]
    public void UnprovenUpsert_RemainsOutsideHostStructuralAdmission(string conflict)
    {
        var statement = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice') " + conflict,
            SqlAgentToolType.Postgres).Statement;

        Assert.False(TypedDmlRuntime.SupportsStatement(statement));
        Assert.Throws<NotSupportedException>(() =>
            TypedDmlRuntime.EnsureSupportedStatement(statement));
    }
}
