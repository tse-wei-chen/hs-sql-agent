using System.ComponentModel;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Temporary namespace marker while callers migrate to <c>HsSqlAgent.SqlCore.Core.Lowering</c>.
/// Compiler lowering types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Lowering. This marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyLoweringNamespaceMarker
{
}
