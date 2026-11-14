using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreFirebirdDmlTargetAliasTests
{
    [Theory]
    [InlineData("UPDATE users AS u SET name = 'Alice' WHERE u.id = 1", SqlStatementKind.Update)]
    [InlineData("DELETE FROM users AS u WHERE u.id = 1", SqlStatementKind.Delete)]
    public void Firebird_TargetAlias_ParsesBindsAndRenders(
        string sql,
        SqlStatementKind expectedKind)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Firebird);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-target-alias-v1"));

        Assert.Equal(expectedKind, command.Kind);
        Assert.Contains(" AS ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("u", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Firebird_TargetAlias_HidesOriginalTargetQualifier()
    {
        var error = Assert.ThrowsAny<Exception>(() =>
        {
            var parsed = CoreSqlTextParser.ParseDml(
                "UPDATE users AS u SET name = 'Alice' WHERE users.id = 1",
                SqlAgentToolType.Firebird);

            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("firebird-target-alias-scope-v1"));
        });

        Assert.Contains("users", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alias", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Postgres_TargetAlias_CrossLowersToFirebird()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users AS u SET name = 'Alice' WHERE u.id = 1",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("postgres-firebird-target-alias-v1"));

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AS ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Firebird_TargetAlias_CrossLowersToPostgres()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users AS u WHERE u.id = 1",
            SqlAgentToolType.Firebird);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("firebird-postgres-target-alias-v1"));

        Assert.Contains("DELETE FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AS ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_FirebirdTargetAlias_IsDeclared()
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Firebird).Capabilities,
            item => item.Id == "dml.target_alias");

        Assert.NotEqual(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains("Firebird", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
