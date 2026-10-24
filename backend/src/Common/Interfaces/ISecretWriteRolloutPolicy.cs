namespace Common.Interfaces;

/// <summary>
/// Read-only operator rollout switches. Never infer full-fleet compatibility
/// from a single node's service configuration.
/// </summary>
public interface ISecretWriteRolloutPolicy
{
    bool EnableV2DatabasePasswordWrites { get; }
    bool EnableV2DurableDmlWrites { get; }
}
