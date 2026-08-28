using System.Text.RegularExpressions;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Renders structured Core identifiers directly for the target SQL dialect. The native renderer
/// owns quote intent and identifier case folding; no query-builder IR is involved.
/// </summary>
internal static class CoreIdentifierSqlRenderer
{
    public static string Render(
        SqlIdentifier identifier,
        SqlAgentToolType provider,
        bool allowWildcard)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (identifier.Parts.IsDefaultOrEmpty)
            throw new SqlCompilationException("SQL identifier has no parts.");

        var rendered = new string[identifier.Parts.Length];
        for (var i = 0; i < identifier.Parts.Length; i++)
        {
            var part = identifier.Parts[i];
            var wildcard = part.Value == "*" && !part.WasQuoted;
            if (wildcard)
            {
                if (!allowWildcard || i != identifier.Parts.Length - 1)
                {
                    throw new SqlCompilationException(
                        "SQL wildcard is only valid as the final expression identifier part.");
                }

                rendered[i] = "*";
                continue;
            }

            ValidatePart(part, "identifier");
            rendered[i] = Quote(NormalizePart(part, provider), provider);
        }

        return string.Join('.', rendered);
    }

    public static string NormalizeSinglePart(
        SqlIdentifier identifier,
        SqlAgentToolType provider,
        string label)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (identifier.Parts.Length != 1)
            throw new SqlCompilationException(label + " must be an unqualified identifier.");

        var part = identifier.Parts[0];
        if (part.Value == "*" && !part.WasQuoted)
            throw new SqlCompilationException(label + " cannot be a wildcard.");

        ValidatePart(part, label);
        return NormalizePart(part, provider);
    }

    public static string RenderAlias(IdentifierPart alias, SqlAgentToolType provider)
    {
        ValidatePart(alias, "alias");
        return Quote(NormalizePart(alias, provider), provider);
    }

    public static string NormalizeAlias(IdentifierPart alias, SqlAgentToolType provider)
    {
        ValidatePart(alias, "alias");
        return NormalizePart(alias, provider);
    }

    public static string Quote(string value, SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.MySQL =>
            "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`",
        SqlAgentToolType.MsSqlServer =>
            "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]",
        SqlAgentToolType.Postgres or
        SqlAgentToolType.Sqlite or
        SqlAgentToolType.Oracle or
        SqlAgentToolType.Firebird =>
            "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        _ => throw new SqlCompilationException(
            "Unsupported target provider '" + provider + "'.")
    };

    private static void ValidatePart(IdentifierPart part, string label)
    {
        if (part.WasQuoted)
        {
            if (part.Value.Length == 0 || part.Value.Any(char.IsControl))
            {
                throw new SqlCompilationException(
                    "Unsafe quoted SQL " + label + " '" + part.Value + "'.");
            }

            return;
        }

        if (!Regex.IsMatch(
                part.Value,
                @"^[A-Za-z_][A-Za-z0-9_$]*$",
                RegexOptions.CultureInvariant))
        {
            throw new SqlCompilationException(
                "Unsafe SQL " + label + " '" + part.Value + "'.");
        }
    }

    private static string NormalizePart(
        IdentifierPart part,
        SqlAgentToolType provider) =>
        part.PreserveSpelling
            ? part.Value
            : SqlIdentifierDialectRules.CanonicalPart(part, provider);
}
