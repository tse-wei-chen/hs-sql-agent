namespace Auth.Service.Models;

public sealed class AuthRuntimeSessionState
{
    public Guid Id { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public sealed class AuthRuntimeState
{
    public bool Exists { get; init; }
    public bool IsActive { get; init; }
    public int SecurityVersion { get; init; }
    public AuthRuntimeSessionState[] ActiveSessions { get; init; } = [];
    public bool IsBarrier { get; init; }
    public string? BarrierReason { get; init; }
}
