using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;

namespace SqlAgent.Service.Core.Analysis;

/// <summary>
/// DML validator that reuses the common validator for authorization and query capabilities while
/// preserving the canonical INSERT statement as the validated output.
/// </summary>
public sealed class CoreDmlPlanValidator : ISqlPlanValidator
{
    private readonly CoreSqlPlanValidator _common = new();

    public ValidatedSqlPlan Validate(
        CanonicalStatement statement,
        SqlPlanValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.Statement is not InsertStatement insert)
            return _common.Validate(statement, context);

        ValidateInsertShape(insert);
        var validationCarrier = insert.Source switch
        {
            InsertQuerySource querySource => querySource.Query,
            InsertValuesSource values => CreateValuesCarrier(values),
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source during validation: {insert.Source.GetType().Name}")
        };

        var validatedCarrier = _common.Validate(
            statement with { Statement = validationCarrier },
            context);

        // The common validator may canonicalize query-only structures such as CTE column aliases.
        // Preserve that validated query in INSERT ... SELECT rather than returning the stale source
        // shape and re-introducing an unsupported lowerer form after validation.
        var validatedInsert = insert.Source is InsertQuerySource querySource
            ? insert with
            {
                Source = querySource with { Query = validatedCarrier.Statement }
            }
            : insert;

        return new ValidatedSqlPlan(
            validatedInsert,
            statement.Facts,
            statement.SourceDialect,
            statement.TargetProvider,
            context.PolicyVersion);
    }

    private static void ValidateInsertShape(InsertStatement insert)
    {
        if (insert.Columns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT requires at least one target column.");
        if (insert.Columns.Any(column => column.Parts.Length != 1))
            throw new SqlCompilationException("INSERT target columns must be unqualified.");
        if (insert.Source is InsertValuesSource values)
        {
            if (values.Rows.IsDefaultOrEmpty)
                throw new SqlCompilationException("INSERT VALUES requires at least one row.");
            foreach (var row in values.Rows)
            {
                if (row.Length != insert.Columns.Length)
                    throw new SqlCompilationException("INSERT VALUES row width does not match target column count.");
                if (row.Any(value => value is not LiteralExpr))
                    throw new SqlCompilationException("INSERT VALUES currently accepts canonical literal values only.");
            }
        }
    }

    private static SelectStatement CreateValuesCarrier(InsertValuesSource values)
    {
        var first = values.Rows[0];
        return new SelectStatement(
            Ctes: ImmutableArray<CteDefinition>.Empty,
            Distinct: false,
            Select: first.Select((value, index) => new SelectItem(value, $"v{index}", value.Span)).ToImmutableArray(),
            From: null,
            Joins: ImmutableArray<JoinSource>.Empty,
            Where: null,
            GroupBy: ImmutableArray<SqlExpr>.Empty,
            Having: null,
            OrderBy: ImmutableArray<OrderByItem>.Empty,
            Limit: null,
            Offset: null,
            Span: SourceSpan.Unknown);
    }
}
