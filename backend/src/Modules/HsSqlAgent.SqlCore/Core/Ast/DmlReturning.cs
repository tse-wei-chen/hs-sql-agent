using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Ast;

/// <summary>
/// Canonical semantic model for rows produced by DML RETURNING/OUTPUT-style clauses.
/// Target columns and a lone wildcard form the currently proven portable lowering subset.
/// Richer result expressions have an explicit semantic node so provider capability work can evolve
/// without overloading SqlIdentifier shape or accidentally treating expressions as portable.
/// </summary>
public abstract record DmlReturningItem(SourceSpan Span) : SqlNode(Span);

public sealed record DmlReturningColumnItem(
    SqlIdentifier Identifier,
    SourceSpan Span) : DmlReturningItem(Span);

public sealed record DmlReturningWildcardItem(SourceSpan Span) : DmlReturningItem(Span);

/// <summary>
/// A canonical DML result expression. Merely representing this node does not authorize a provider
/// lowering: the current portable RETURNING contract remains column/wildcard-only and lowering must
/// fail closed until expression semantics are proven for the source/target provider profile.
/// </summary>
public sealed record DmlReturningExpressionItem(
    SqlExpr Expression,
    IdentifierPart? Alias,
    SourceSpan Span) : DmlReturningItem(Span);

public static class DmlReturningProjection
{
    public static ImmutableArray<DmlReturningItem> FromColumns(
        ImmutableArray<SqlIdentifier> columns)
    {
        if (columns.IsDefaultOrEmpty)
            return ImmutableArray<DmlReturningItem>.Empty;

        var items = ImmutableArray.CreateBuilder<DmlReturningItem>(columns.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wildcard = false;

        foreach (var column in columns)
        {
            if (column.Parts.Length != 1)
            {
                throw new SqlCompilationException(
                    "Portable DML RETURNING accepts unqualified target columns only.");
            }

            var part = column.Parts[0];
            var isWildcard = part.Value == "*" && !part.WasQuoted;
            wildcard |= isWildcard;
            if (!seen.Add(part.Value))
            {
                throw new SqlCompilationException(
                    $"RETURNING column '{part.Value}' is declared more than once.");
            }

            items.Add(isWildcard
                ? new DmlReturningWildcardItem(column.Span)
                : new DmlReturningColumnItem(column, column.Span));
        }

        if (wildcard && columns.Length != 1)
        {
            throw new SqlCompilationException(
                "RETURNING * cannot be mixed with explicit RETURNING columns in the portable Core contract.");
        }

        return items.ToImmutable();
    }
}
