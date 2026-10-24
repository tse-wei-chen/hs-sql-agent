namespace HsSqlAgent.Server.Services;

/// <summary>
/// Immutable per-node rollout policy. Defaults to legacy-only writes.
/// This is an operator-controlled switch, not proof of fleet readiness.
/// </summary>
public sealed record SecretEncryptionWritePolicy(
    bool EnableV2DurableDmlWrites,
    bool EnableV2DatabasePasswordWrites) : Common.Interfaces.ISecretWriteRolloutPolicy;
