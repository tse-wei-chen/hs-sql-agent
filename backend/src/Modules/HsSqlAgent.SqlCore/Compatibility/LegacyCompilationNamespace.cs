using System.ComponentModel;

namespace SqlAgent.Service.Core.Compilation;

/// <summary>
/// Temporary namespace marker used while callers migrate to
/// <c>HsSqlAgent.SqlCore.Core.Compilation</c>. Compiler command types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Compilation. This compatibility marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyCompilationNamespaceMarker
{
}
