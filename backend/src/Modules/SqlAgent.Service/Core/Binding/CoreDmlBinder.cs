using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Binding;

/// <summary>
/// DML binder that delegates query scoping to <see cref="SqlAstBinder"/> while handling INSERT's
/// write target explicitly. UPDATE/DELETE continue through the common binder unchanged.
/// </summary>
public sealed class CoreDmlBinder : ISqlBinder
{
    private readonly SqlAstBinder _queryBinder = new();

    public BoundStatement Bind(ParsedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.Statement is not InsertStatement insert)
            return _queryBinder.Bind(statement);

        ValidateInsert(insert);
        var targetName = IdentifierText(insert.Target.Name);
        var targetFacts = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, targetName);

        return insert.Source switch
        {
            InsertValuesSource values => new BoundStatement(
                insert with
                {
                    Source = values with
                    {
                        Rows = values.Rows.Select(row => row.Select(BindInsertValue).ToImmutableArray()).ToImmutableArray()
                    }
                },
                new QueryFacts(
                    targetFacts,
                    ImmutableArray<QueryAliasFact>.Empty,
                    ContainsSubquery: false,
                    ContainsCte: false),
                statement.SourceDialect),

            InsertQuerySource querySource => BindInsertQuery(
                statement.SourceDialect,
                insert,
                querySource,
                targetName),

            _ => throw new InvalidOperationException(
                $"Unsupported INSERT source while binding: {insert.Source.GetType().Name}")
        };
    }

    private BoundStatement BindInsertQuery(
        SqlAgentToolType sourceDialect,
        InsertStatement insert,
        InsertQuerySource querySource,
        string targetName)
    {
        var boundQuery = _queryBinder.Bind(new ParsedStatement(querySource.Query, sourceDialect));
        var tables = boundQuery.Facts.ReferencedTables.Add(targetName);
        return new BoundStatement(
            insert with
            {
                Source = querySource with { Query = boundQuery.Statement }
            },
            boundQuery.Facts with { ReferencedTables = tables, ContainsSubquery = true },
            sourceDialect);
    }

    private static SqlExpr BindInsertValue(SqlExpr expression) => expression switch
    {
        LiteralExpr literal => literal,
        _ => throw new InvalidOperationException(
            $"INSERT VALUES currently accepts literal canonical expressions only, not {expression.GetType().Name}.")
    };

    private static void ValidateInsert(InsertStatement insert)
    {
        if (insert.Columns.IsDefaultOrEmpty)
            throw new InvalidOperationException("INSERT requires at least one target column.");
        if (insert.Columns.Any(column => column.Parts.Length != 1))
            throw new InvalidOperationException("INSERT target columns must be unqualified.");
        if (insert.Source is InsertValuesSource { Rows.IsDefaultOrEmpty: true })
            throw new InvalidOperationException("INSERT VALUES requires at least one row.");
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
