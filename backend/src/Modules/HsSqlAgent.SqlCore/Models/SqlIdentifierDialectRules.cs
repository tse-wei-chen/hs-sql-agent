namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single dialect contract for SQL identifier identity. Quoted parts preserve exact spelling.
/// Unquoted PostgreSQL identifiers fold to lower case, Oracle/Firebird identifiers fold to upper
/// case, and MySQL/SQL Server/SQLite identifier lookup remains case-insensitive.
/// Rendering may separately preserve source spelling when an AST node explicitly requests it.
/// </summary>
internal static class SqlIdentifierDialectRules
{
    internal static StringComparer Comparer(SqlAgentToolType provider) =>
        provider is
            SqlAgentToolType.Postgres
            or SqlAgentToolType.Oracle
            or SqlAgentToolType.Firebird
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

    internal static string CanonicalPart(
        IdentifierPart part,
        SqlAgentToolType provider)
    {
        if (part.WasQuoted)
            return part.Value;

        return provider switch
        {
            SqlAgentToolType.Postgres => part.Value.ToLowerInvariant(),
            SqlAgentToolType.Oracle or SqlAgentToolType.Firebird =>
                part.Value.ToUpperInvariant(),
            _ => part.Value
        };
    }

    internal static bool Equivalent(
        IdentifierPart left,
        IdentifierPart right,
        SqlAgentToolType provider) =>
        Comparer(provider).Equals(
            CanonicalPart(left, provider),
            CanonicalPart(right, provider));
}
