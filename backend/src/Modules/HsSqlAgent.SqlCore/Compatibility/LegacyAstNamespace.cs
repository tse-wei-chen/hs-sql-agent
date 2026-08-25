using System.ComponentModel;

namespace SqlAgent.Service.Core.Ast;

/// <summary>
/// Temporary namespace marker used while callers migrate to
/// <c>HsSqlAgent.SqlCore.Core.Ast</c>. Compiler AST types are no longer declared here.
/// </summary>
[Obsolete("Use HsSqlAgent.SqlCore.Core.Ast. This compatibility marker will be removed after namespace migration.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyAstNamespaceMarker
{
}
