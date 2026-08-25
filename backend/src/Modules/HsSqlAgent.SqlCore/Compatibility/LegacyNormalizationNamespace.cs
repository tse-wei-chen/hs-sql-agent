using System.ComponentModel;

namespace SqlAgent.Service.Core.Normalization;

/// <summary>
/// Temporary namespace marker while callers migrate to <c>HsSqlAgent.SqlCore.Core.Normalization</c>.
/// Compiler normalization types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Normalization. This marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyNormalizationNamespaceMarker
{
}
