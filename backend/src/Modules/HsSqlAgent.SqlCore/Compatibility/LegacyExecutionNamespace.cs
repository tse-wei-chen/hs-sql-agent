using System.ComponentModel;
using HsSqlAgent.SqlCore.Core.Compilation;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Temporary namespace marker used while compiler callers migrate to
/// <c>HsSqlAgent.SqlCore.Core.Execution</c>. Runtime execution types remain owned by SqlAgent.Service;
/// compiler execution helpers are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Execution for compiler execution helpers.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyCoreExecutionNamespaceMarker
{
}

/// <summary>
/// Temporary forwarding shim for the last fully-qualified compiler caller.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Execution.DmlFingerprintService. This shim will be removed after Lowering namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DmlFingerprintService
{
    public static string ComputePlanFingerprint(CompiledSqlCommand mutationCommand, string policyVersion) =>
        HsSqlAgent.SqlCore.Core.Execution.DmlFingerprintService.ComputePlanFingerprint(mutationCommand, policyVersion);

    public static string ComputeRowSetFingerprint(IEnumerable<IReadOnlyList<object?>> orderedKeys) =>
        HsSqlAgent.SqlCore.Core.Execution.DmlFingerprintService.ComputeRowSetFingerprint(orderedKeys);

    public static string ComputeUnorderedRowSetFingerprint(IEnumerable<IReadOnlyList<object?>> keys) =>
        HsSqlAgent.SqlCore.Core.Execution.DmlFingerprintService.ComputeUnorderedRowSetFingerprint(keys);
}
