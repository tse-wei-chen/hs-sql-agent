using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// A test-only persistent authorization contract for manual proxy constructions.
/// Never registered by the production Admin Store services.
/// </summary>
internal sealed class TestMcpDmlCommitAuthorizationVerifier : IMcpDmlCommitAuthorizationVerifier
{
    public Task<McpDmlCommitAuthorizationSnapshot> CaptureAsync(
        McpSqlExecutionScope scope,
        string originalConnectionString,
        int? customToolId,
        int? customToolRevisionId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new McpDmlCommitAuthorizationSnapshot(
            "manual-test-stored-authorization", null));
}
