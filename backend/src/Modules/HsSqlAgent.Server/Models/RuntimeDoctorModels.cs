namespace HsSqlAgent.Server.Models;

public sealed record RuntimeDoctorResponse(
    string OverallStatus,
    string Environment,
    string DeploymentMode,
    DateTime GeneratedAtUtc,
    IReadOnlyList<RuntimeDoctorCheck> Checks);

public sealed record RuntimeDoctorCheck(
    string Id,
    string Category,
    string Status,
    string Title,
    string Detail,
    string? Action = null);
