using System.ComponentModel;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Temporary namespace marker used while compiler callers migrate to
/// <c>HsSqlAgent.SqlCore.Core.Execution</c>. Runtime execution types remain owned by SqlAgent.Service;
/// DmlFingerprintService is no longer declared in this namespace.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Execution for compiler execution helpers.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyCoreExecutionNamespaceMarker
{
}
