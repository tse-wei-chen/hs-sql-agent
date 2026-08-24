using System.Text.RegularExpressions;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Renders structured Core identifiers without flattening quoted parts. This is shared by DML
/// lowering paths that must preserve the same quote/case semantics as query lowering while still
/// letting SqlKata own statement structure.
/// </summary>
internal static class CoreIdentifierSqlRenderer
{
    public static string Render(SqlIdentifier identifier, Compiler compiler, bool allowWildcard)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(compiler);
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
                    throw new SqlCompilationException("SQL wildcard is only valid as the final expression identifier part.");
                rendered[i] = "*";
                continue;
            }

            ValidatePart(part, "identifier");
            rendered[i] = compiler.WrapValue(NormalizePart(part, compiler));
        }

        return string.Join('.', rendered);
    }

    public static string NormalizeSinglePart(SqlIdentifier identifier, Compiler compiler, string label)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(compiler);
        if (identifier.Parts.Length != 1)
            throw new SqlCompilationException($"{label} must be an unqualified identifier.");

        var part = identifier.Parts[0];
        if (part.Value == "*" && !part.WasQuoted)
            throw new SqlCompilationException($"{label} cannot be a wildcard.");
        ValidatePart(part, label);
        return NormalizePart(part, compiler);
    }

    private static void ValidatePart(IdentifierPart part, string label)
    {
        if (part.WasQuoted)
        {
            if (part.Value.Length == 0 || part.Value.Any(char.IsControl))
                throw new SqlCompilationException($"Unsafe quoted SQL {label} '{part.Value}'.");
            return;
        }

        if (!Regex.IsMatch(part.Value, @"^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe SQL {label} '{part.Value}'.");
    }

    private static string NormalizePart(IdentifierPart part, Compiler compiler)
    {
        if (part.WasQuoted || part.PreserveSpelling) return part.Value;
        return compiler switch
        {
            PostgresCompiler => part.Value.ToLowerInvariant(),
            OracleCompiler or FirebirdCompiler => part.Value.ToUpperInvariant(),
            _ => part.Value
        };
    }
}
