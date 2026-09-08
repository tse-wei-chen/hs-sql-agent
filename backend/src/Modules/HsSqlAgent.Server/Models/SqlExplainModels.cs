using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Binding;

namespace HsSqlAgent.Server.Models;

public sealed class SqlExplainRequest
{
    public int DbManagementId { get; set; }
    public string Sql { get; set; } = string.Empty;
    public string StatementType { get; set; } = "Query";
    public string? SourceDialect { get; set; }
    public int? AccessKeyId { get; set; }
}

public sealed record SqlExplainResponse(
    bool Success,
    string SimulationMode,
    bool Executed,
    string StatementType,
    string SourceDialect,
    string TargetProvider,
    SqlExplainScope Scope,
    SqlExplainPolicySnapshot Policy,
    IReadOnlyList<SqlExplainStatement> Statements,
    SqlExplainFailure? Failure);

public sealed record SqlExplainScope(
    int? AccessKeyId,
    string? AccessKeyName,
    bool KeyActive,
    string RequiredTool,
    bool ToolAllowed,
    string ToolScope,
    string TableScope,
    IReadOnlyList<string> AllowedTables);

public sealed record SqlExplainPolicySnapshot(
    int QueryMaxRows,
    int QueryTimeoutSeconds,
    bool RequireWhereForUpdate,
    bool RequireWhereForDelete,
    bool AllowFullTableUpdate,
    bool AllowFullTableDelete,
    int DmlMaxAffectedRows,
    bool DmlAffectedRowLimitEvaluated);

public sealed record SqlExplainStatement(
    int Index,
    string Kind,
    string RenderedSql,
    bool ReturnsRows,
    string PlanFingerprint,
    IReadOnlyList<SqlExplainParameter> Parameters,
    SqlExplainQueryFacts? QueryFacts,
    SqlExplainEvidence? Evidence);

public sealed record SqlExplainParameter(string Name, object? Value);

public sealed record SqlExplainQueryFacts(
    IReadOnlyList<string> ReferencedTables,
    bool ContainsCte,
    bool ContainsSubquery);

public sealed record SqlExplainFailure(
    string Code,
    string Message,
    string Stage,
    bool Retryable,
    IReadOnlyList<SqlExplainDiagnostic> Diagnostics,
    SqlExplainEvidence? Evidence);

public sealed record SqlExplainDiagnostic(
    string Code,
    string Stage,
    string Category,
    string Message,
    int? Start,
    int? Length,
    int? End);

public sealed record SqlExplainEvidence(
    string SchemaVersion,
    string CapabilityMatrixVersion,
    string Verdict,
    string DecisionBoundary,
    string DecisionCode,
    string? PlanFingerprint,
    string EvidenceFingerprint,
    SqlExplainProfile SourceProfile,
    SqlExplainProfile TargetProfile,
    SqlExplainPolicyEvidence Policy,
    IReadOnlyList<SqlExplainCapabilityEvidence> SourceCapabilities,
    IReadOnlyList<SqlExplainCapabilityEvidence> TargetCapabilities,
    IReadOnlyList<SqlExplainAssuranceEvidence> Assurances);

public sealed record SqlExplainProfile(
    string Provider,
    string? ServerVersion,
    int? CompatibilityLevel,
    IReadOnlyList<string> SessionModes,
    IReadOnlyList<SqlExplainSettingEvidence> SessionSettings);

public sealed record SqlExplainPolicyEvidence(
    string PolicyVersion,
    int QueryMaxRows,
    bool RequireUpdatePredicate,
    bool RequireDeletePredicate,
    IReadOnlyList<string> AllowedTables);

public sealed record SqlExplainCapabilityEvidence(
    string Side,
    string Id,
    string Category,
    string Status,
    string Detail);

public sealed record SqlExplainAssuranceEvidence(
    string Kind,
    IReadOnlyList<SqlExplainSettingEvidence> Details);

public sealed record SqlExplainSettingEvidence(string Name, string Value);

internal static class SqlExplainModelMapper
{
    internal static SqlExplainPolicySnapshot Policy(Admin.Service.Models.SecurityPolicyModel policy) =>
        new(
            policy.QueryMaxRows,
            policy.QueryTimeoutSeconds,
            policy.RequireWhereForUpdate,
            policy.RequireWhereForDelete,
            policy.AllowFullTableUpdate,
            policy.AllowFullTableDelete,
            policy.DmlMaxAffectedRows,
            false);

    internal static SqlExplainStatement Statement(
        int index,
        CompiledSqlCommand command,
        QueryFacts? facts = null) =>
        new(
            index,
            command.Kind.ToString(),
            command.Sql,
            command.ReturnsRows,
            command.PlanFingerprint,
            command.Parameters
                .Select(parameter => new SqlExplainParameter(parameter.Name, parameter.Value))
                .ToArray(),
            facts is null
                ? null
                : new SqlExplainQueryFacts(
                    facts.ReferencedTables.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    facts.ContainsCte,
                    facts.ContainsSubquery),
            Evidence(command.CompileEvidence));

    internal static SqlExplainEvidence? Evidence(SqlCompileEvidence? evidence)
    {
        if (evidence is null) return null;

        return new SqlExplainEvidence(
            evidence.SchemaVersion,
            evidence.CapabilityMatrixVersion,
            evidence.Verdict.ToString(),
            evidence.DecisionBoundary.ToString(),
            evidence.DecisionCode,
            evidence.PlanFingerprint,
            evidence.EvidenceFingerprint,
            Profile(evidence.SourceProfile),
            Profile(evidence.TargetProfile),
            new SqlExplainPolicyEvidence(
                evidence.Policy.PolicyVersion,
                evidence.Policy.QueryMaxRows,
                evidence.Policy.RequireUpdatePredicate,
                evidence.Policy.RequireDeletePredicate,
                evidence.Policy.AllowedTables.ToArray()),
            evidence.SourceCapabilities
                .Select(Capability)
                .ToArray(),
            evidence.TargetCapabilities
                .Select(Capability)
                .ToArray(),
            evidence.Assurances
                .Select(assurance => new SqlExplainAssuranceEvidence(
                    assurance.Kind,
                    assurance.Details.Select(Setting).ToArray()))
                .ToArray());
    }

    internal static SqlExplainDiagnostic Diagnostic(SqlDiagnostic diagnostic) =>
        new(
            diagnostic.Code,
            diagnostic.Stage.ToString(),
            diagnostic.Category.ToString(),
            diagnostic.Message,
            diagnostic.Span?.Start,
            diagnostic.Span?.Length,
            diagnostic.Span?.End);

    private static SqlExplainProfile Profile(SqlCompileProfileEvidence profile) =>
        new(
            profile.Provider.ToString(),
            profile.ServerVersion,
            profile.CompatibilityLevel.HasValue ? profile.CompatibilityLevel.Value : null,
            profile.SessionModes.ToArray(),
            profile.SessionSettings.Select(Setting).ToArray());

    private static SqlExplainCapabilityEvidence Capability(SqlCompileCapabilityEvidence capability) =>
        new(
            capability.Side.ToString(),
            capability.Id,
            capability.Category,
            capability.Status.ToString(),
            capability.Detail);

    private static SqlExplainSettingEvidence Setting(SqlCompileSettingEvidence setting) =>
        new(setting.Name, setting.Value);
}
