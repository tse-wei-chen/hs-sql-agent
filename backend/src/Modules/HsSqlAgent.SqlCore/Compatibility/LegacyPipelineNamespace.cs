using System.ComponentModel;

namespace SqlAgent.Service.Core.Pipeline;

/// <summary>
/// Temporary namespace marker used while callers migrate to
/// <c>HsSqlAgent.SqlCore.Core.Pipeline</c>. Compiler pipeline types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Pipeline. This compatibility marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyPipelineNamespaceMarker
{
}
