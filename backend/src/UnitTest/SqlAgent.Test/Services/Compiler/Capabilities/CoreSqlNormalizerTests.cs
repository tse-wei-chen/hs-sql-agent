using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlNormalizerTests
{
    [Fact]
    public void Normalize_SqlServerLen_PreservesTrailingSpaceSemanticsAcrossDialects()
    {
        var command = Compile(
            "SELECT LEN(name) FROM users",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres,
            "fsharp-normalize-len-v1");

        Assert.Contains("LENGTH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RTRIM", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_CurrentTimestampTemplate_LowersToTargetTemporalPrimitive()
    {
        var command = Compile(
            "SELECT NOW() FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            "fsharp-normalize-current-timestamp-v1");

        Assert.DoesNotContain("NOW(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CURRENT_DATE", "DATE")]
    [InlineData("CURRENT_TIME", "TIME")]
    public void Normalize_CurrentTemporalFunctions_DoNotLeakCanonicalImplementationNames(
        string sourceName,
        string expectedTargetToken)
    {
        var command = Compile(
            $"SELECT {sourceName}() FROM users",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.MsSqlServer,
            "fsharp-normalize-current-temporal-v1");

        Assert.DoesNotContain("CORE_CURRENT_", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedTargetToken, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target,
        string policyVersion)
    {
        var validation = new SqlPlanValidationContext(
            policyVersion,
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        return SqlCoreFacade.CompileQuery(
            sql,
            source,
            target,
            validation,
            new SqlExecutionPlanPolicy());
    }
}
