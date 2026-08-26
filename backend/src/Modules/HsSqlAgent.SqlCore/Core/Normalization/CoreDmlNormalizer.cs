namespace HsSqlAgent.SqlCore.Core.Normalization;

/// <summary>
/// DML normalizer that keeps INSERT structure explicit and delegates INSERT value/query expression
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

        InsertSource normalizedSource = insert.Source switch
        {
            InsertValuesSource values => NormalizeValues(
                statement,
                values,
                targetProvider),
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

    private InsertValuesSource NormalizeValues(
        BoundStatement parent,
        InsertValuesSource values,
        SqlAgentToolType targetProvider)
    {
        var carrier = CoreInsertValuesCarrier.CreateExpressionCarrier(values);
        var normalized = _queryNormalizer.Normalize(
            new BoundStatement(carrier, parent.Facts, parent.SourceDialect),
            targetProvider);
        return CoreInsertValuesCarrier.RestoreFromExpressionCarrier(
            values,
            normalized.Statement);
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
}
