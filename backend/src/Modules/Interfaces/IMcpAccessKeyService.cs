using Modules.Models;
using ToolBox.Models;

namespace Modules.Interfaces;

public interface IMcpAccessKeyService
{
    Task<McpAccessKeyIssueResult> IssueKeyAsync(
        IssueMcpAccessKeyRequest request,
        string? actorId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<McpAccessKeyListItem>> ListKeysAsync(CancellationToken cancellationToken = default);
    Task<bool> RevokeKeyAsync(int id, string? actorId, CancellationToken cancellationToken = default);
    Task<McpAccessKeyValidationResult> ValidateAsync(string rawKey, CancellationToken cancellationToken = default);
    Task TouchLastUsedAsync(int keyId, CancellationToken cancellationToken = default);
}
