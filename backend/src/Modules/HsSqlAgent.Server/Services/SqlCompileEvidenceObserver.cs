using System.Diagnostics;
using HsSqlAgent.SqlCore.Core.Compilation;

namespace HsSqlAgent.Server.Services;

public interface ISqlCompileEvidenceObserver
{
    void Observe(SqlCompileEvidence? evidence);
    void Observe(Exception exception);
}

public sealed class SqlCompileEvidenceObserver(
    ILogger<SqlCompileEvidenceObserver> logger,
    IHsSqlAgentMetrics metrics) : ISqlCompileEvidenceObserver
{
    public const string ActivitySourceName = "HsSqlAgent.Server.SqlCompiler";

    private static readonly ActivitySource ActivitySource =
        new(ActivitySourceName, "1.0.0");

    public void Observe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Observe(FindEvidence(exception));
    }

    public void Observe(SqlCompileEvidence? evidence)
    {
        if (evidence is null)
            return;

        var verdict = evidence.Verdict.ToString();
        var boundary = evidence.DecisionBoundary.ToString();
        var sourceProvider = evidence.SourceProfile.Provider.ToString();
        var targetProvider = evidence.TargetProfile.Provider.ToString();

        metrics.RecordSqlCompile(
            verdict,
            boundary,
            evidence.DecisionCode,
            sourceProvider,
            targetProvider);

        using var activity = ActivitySource.StartActivity(
            "sql.compile.decision",
            ActivityKind.Internal);
        activity?.SetTag("sql.compile.verdict", verdict);
        activity?.SetTag("sql.compile.boundary", boundary);
        activity?.SetTag("sql.compile.decision_code", evidence.DecisionCode);
        activity?.SetTag("sql.compile.source_provider", sourceProvider);
        activity?.SetTag("sql.compile.target_provider", targetProvider);
        activity?.SetTag("sql.compile.schema_version", evidence.SchemaVersion);
        activity?.SetTag("sql.compile.capability_matrix_version", evidence.CapabilityMatrixVersion);
        activity?.SetTag("sql.compile.evidence_fingerprint", evidence.EvidenceFingerprint);
        activity?.SetStatus(
            evidence.Verdict == SqlCompileVerdict.Rejected
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok,
            evidence.Verdict == SqlCompileVerdict.Rejected
                ? evidence.DecisionCode
                : null);

        var traceId = Activity.Current?.TraceId.ToString();
        if (evidence.Verdict == SqlCompileVerdict.Rejected)
        {
            logger.LogWarning(
                "SQL compile rejected at {DecisionBoundary} with {DecisionCode}; {SourceProvider} -> {TargetProvider}; evidence {EvidenceFingerprint}; matrix {CapabilityMatrixVersion}; trace {TraceId}",
                boundary,
                evidence.DecisionCode,
                sourceProvider,
                targetProvider,
                evidence.EvidenceFingerprint,
                evidence.CapabilityMatrixVersion,
                traceId);
            return;
        }

        logger.LogDebug(
            "SQL compile translated; {SourceProvider} -> {TargetProvider}; evidence {EvidenceFingerprint}; matrix {CapabilityMatrixVersion}; trace {TraceId}",
            sourceProvider,
            targetProvider,
            evidence.EvidenceFingerprint,
            evidence.CapabilityMatrixVersion,
            traceId);
    }

    private static SqlCompileEvidence? FindEvidence(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var evidence = SqlCompileEvidence.TryGetFromException(current);
            if (evidence is not null)
                return evidence;
        }

        return null;
    }
}
