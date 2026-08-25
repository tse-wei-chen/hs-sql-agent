using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using SqlAgent.Service.Enums;

namespace HsSqlAgent.SqlCore.Core.Binding;

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

        return insert.Source switch
        {
            InsertValuesSource values => BindInsertValues(
                statement.SourceDialect,
                insert,
                values,
                targetName),

            InsertQuerySource querySource => BindInsertQuery(
                statement.SourceDialect,
                insert,
                querySource,
                targetName),

            _ => throw new InvalidOperationException(
                $"Unsupported INSERT source while binding: {insert.Source.GetType().Name}")
        };
    }

    private BoundStatement BindInsertValues(
        SqlAgentToolType sourceDialect,
        InsertStatement insert,
        InsertValuesSource values,
        string targetName)
    {
        var carrier = CoreInsertValuesCarrier.CreateExpressionCarrier(values);
        var boundCarrier = _queryBinder.Bind(new ParsedStatement(carrier, sourceDialect));
        var boundValues = CoreInsertValuesCarrier.RestoreFromExpressionCarrier(
            values,
            boundCarrier.Statement);
        var tables = boundCarrier.Facts.ReferencedTables.Add(targetName);

        return new BoundStatement(
            insert with { Source = boundValues },
            boundCarrier.Facts with { ReferencedTables = tables },
            sourceDialect);
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
