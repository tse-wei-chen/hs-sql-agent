using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreProfileAstRewriteTraversalTests
{
    [Fact]
    public void Compile_MySqlSourceProfile_RewritesConcatThroughDerivedAndScalarSubquery()
    {
        var sourceProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(
                new[] { "PIPES_AS_CONCAT" },
                StringComparer.OrdinalIgnoreCase));

        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT d.full_name FROM " +
            "(SELECT (SELECT first_name || last_name FROM users LIMIT 1) AS full_name FROM users) d",
            SqlAgentToolType.MySQL,
            sourceProfile);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("profile-ast-rewriter-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains(" || ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "__CORE_MYSQL_PIPES_AS_CONCAT__",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServer14Target_RewritesConcatThroughCte()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH names AS (" +
            "SELECT first_name || last_name AS full_name FROM users" +
            ") SELECT full_name FROM names",
            SqlAgentToolType.Postgres);

        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(14, 0),
            CompatibilityLevel: 140);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("profile-ast-rewriter-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

        Assert.Contains(" + ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }
}
