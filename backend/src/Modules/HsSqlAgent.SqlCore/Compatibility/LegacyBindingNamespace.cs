using System.ComponentModel;

namespace SqlAgent.Service.Core.Binding;

/// <summary>
/// Temporary namespace marker while callers migrate to <c>HsSqlAgent.SqlCore.Core.Binding</c>.
/// Compiler binding types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Binding. This marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyBindingNamespaceMarker
{
}
