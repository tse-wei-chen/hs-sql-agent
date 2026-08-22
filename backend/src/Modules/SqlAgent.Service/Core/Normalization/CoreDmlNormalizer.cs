using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Normalization;

/// <summary>
/// DML normalizer that keeps INSERT structure explicit and delegates INSERT..SELECT query
/// normalization to the common Core query normalizer.
/// </summary>
public sealed class CoreDmlNormalizer : ISqlNormalizer
{
    private readonly CoreSqlNormalizer _queryNormalizer = CoreSqlNormalizer.CreateDefault();

    public CanonicalStatement Normalize(BoundStatement statement, SqlAgentToolType targetProvider)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.Statement is not InsertStatement insert)
            return _queryNormalizer.Normalize(statement, targetProvider);

        var normalizedSource = insert.Source switch
        {
            InsertValuesSource values => values with
            {
                Rows = values.Rows
                    .Select(row => row.Select(NormalizeInsertValue).ToImmutableArray())
                    .ToImmutableArray()
            },
            InsertQuerySource querySource => NormalizeQuerySource(
                statement,
                querySource,
                targetProvider),
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source during normalization: {insert.Source.GetType().Name}")
        };

        return new CanonicalStatement(
            insert with { Source = normalizedSource },
            statement.Facts,
            statement.SourceDialect,
            targetProvider);
    }

    private InsertQuerySource NormalizeQuerySource(
        BoundStatement parent,
        InsertQuerySource source,
        SqlAgentToolType targetProvider)
    {
        var normalized = _queryNormalizer.Normalize(
            new BoundStatement(source.Query, parent.Facts, parent.SourceDialect),
            targetProvider);
        return source with { Query = normalized.Statement };
    }

    private static SqlExpr NormalizeInsertValue(SqlExpr expression) => expression switch
    {
        LiteralExpr literal => literal,
        _ => throw new SqlCompilationException(
            $"INSERT VALUES expression '{expression.GetType().Name}' is not canonical literal data.")
    };
}
