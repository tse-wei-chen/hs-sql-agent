using System.ComponentModel;

namespace SqlAgent.Service.Core.Analysis;

/// <summary>
/// Temporary namespace marker while callers migrate to <c>HsSqlAgent.SqlCore.Core.Analysis</c>.
/// Compiler analysis types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Analysis. This marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyAnalysisNamespaceMarker
{
}
