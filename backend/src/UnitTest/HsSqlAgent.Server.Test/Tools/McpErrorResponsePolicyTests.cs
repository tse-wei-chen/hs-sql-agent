using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed class McpErrorResponsePolicyTests
{
    private const string Secret = "Password=super-secret; SQL=SELECT private FROM payments";

    [Theory]
    [InlineData("Query", "execution.failed", "Execution")]
    [InlineData("Metadata", "metadata.failed", "Metadata")]
    [InlineData("Dml", "dml.execution_failed", "Execution")]
    public void RawProviderException_NeverEscapesToMcp(
        string surfaceName, string code, string stage)
    {
        var surface = Enum.Parse<McpErrorSurface>(surfaceName);
        var error = McpErrorResponsePolicy.Classify(
            new IOException(Secret), surface);
        Assert.Equal(code, error.Code);
        Assert.Equal(stage, error.Stage);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("super-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("payments", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQL=", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("Metadata")]
    [InlineData("Dml")]
    public void UnauthorizedError_NeverEchoesUntrustedExceptionText(string surfaceName)
    {
        var surface = Enum.Parse<McpErrorSurface>(surfaceName);
        var error = McpErrorResponsePolicy.Classify(
            new UnauthorizedAccessException(Secret), surface);
        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal("Authorization", error.Stage);
        Assert.DoesNotContain("super-secret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Query", "execution.timeout", "Execution")]
    [InlineData("Metadata", "metadata.timeout", "Metadata")]
    [InlineData("Dml", "execution.timeout", "Execution")]
    public void TimeoutRetainsRetryableCode_WithoutDriverDetails(
        string surfaceName, string code, string stage)
    {
        var surface = Enum.Parse<McpErrorSurface>(surfaceName);
        var error = McpErrorResponsePolicy.Classify(new TimeoutException(Secret), surface);
        Assert.Equal(code, error.Code);
        Assert.Equal(stage, error.Stage);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("super-secret", error.Message);
    }

    [Fact]
    public void SqlCompileEvidence_RetainsStableCodeAndStageWithoutRawMessage()
    {
        const string input = "SELECT 'Password=super-secret' AS value FROM";
        var exception = Record.Exception(() =>
            SqlCoreFacade.CompileQuery(input,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("mcp-error-egress"),
                new SqlExecutionPlanPolicy(10)));
        Assert.NotNull(exception);
        var evidence = SqlCompileEvidence.TryGetFromException(exception);
        Assert.NotNull(evidence);

        var external = McpErrorResponsePolicy.Classify(exception, McpErrorSurface.Query);

        Assert.Equal(evidence.DecisionCode, external.Code);
        Assert.Equal(evidence.DecisionBoundary.ToString(), external.Stage);
        Assert.DoesNotContain("super-secret", external.Message);
    }

    [Theory]
    [InlineData("dml.commit_outcome_unknown")]
    [InlineData("dml.rollback_outcome_unknown")]
    public void UnknownDmlOutcome_RemainsDistinctAndNonRetryable(string code)
    {
        var response = McpErrorResponsePolicy.SafeDmlUnknownOutcomeMessage;
        var error = McpErrorResponsePolicy.ForDmlOutcomeUnknown(code);
        Assert.Equal(code, error.Code);
        Assert.False(error.Retryable);
        Assert.Contains("Do not retry", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, response);
    }
}
