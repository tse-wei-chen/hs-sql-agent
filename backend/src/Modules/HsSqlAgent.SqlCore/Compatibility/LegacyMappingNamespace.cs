using System.ComponentModel;

namespace SqlAgent.Service.Core.Mapping;

/// <summary>
/// Temporary namespace marker while callers migrate to <c>HsSqlAgent.SqlCore.Core.Mapping</c>.
/// Compiler mapping types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Mapping. This marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyMappingNamespaceMarker
{
}
