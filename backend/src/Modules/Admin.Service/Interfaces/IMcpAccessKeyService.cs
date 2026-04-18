using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IMcpAccessKeyService
{
    Task<McpAccessKeyIssueResult> IssueKeyAsync(
        IssueMcpAccessKeyModel request,
        string? actorId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<McpAccessKeyListItem>> ListKeysAsync(CancellationToken cancellationToken = default);
    Task<bool> RevokeKeyAsync(int id, string? actorId, CancellationToken cancellationToken = default);
    Task<McpAccessKeyValidationResult> ValidateAsync(string rawKey, CancellationToken cancellationToken = default);
    Task TouchLastUsedAsync(int keyId, CancellationToken cancellationToken = default);
}
