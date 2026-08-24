using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

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
        {
            var validated = _common.Validate(statement, context);
            switch (validated.Statement)
            {
                case UpdateStatement update:
                    CoreDmlVolatilePredicateValidator.Validate(update.Predicate);
                    break;
                case DeleteStatement delete:
                    CoreDmlVolatilePredicateValidator.Validate(delete.Predicate);
                    break;
            }
            return validated;
        }

        ValidateInsertShape(insert, statement.TargetProvider);
        var validationCarrier = insert.Source switch
        {
            InsertQuerySource insertQuerySource => insertQuerySource.Query,
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
        var validatedInsert = insert.Source is InsertQuerySource originalQuerySource
            ? insert with
            {
                Source = originalQuerySource with { Query = validatedCarrier.Statement }
            }
            : insert;

        return new ValidatedSqlPlan(
            validatedInsert,
            statement.Facts,
            statement.SourceDialect,
            statement.TargetProvider,
            context.PolicyVersion);
    }

    private static void ValidateInsertShape(
        InsertStatement insert,
        SqlAgentToolType provider)
    {
        if (insert.Columns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT requires at least one target column.");
        if (insert.Columns.Any(column => column.Parts.Length != 1))
            throw new SqlCompilationException("INSERT target columns must be unqualified.");

        switch (insert.Source)
        {
            case InsertValuesSource values:
                if (values.Rows.IsDefaultOrEmpty)
                    throw new SqlCompilationException("INSERT VALUES requires at least one row.");
                foreach (var row in values.Rows)
                {
                    if (row.Length != insert.Columns.Length)
                        throw new SqlCompilationException("INSERT VALUES row width does not match target column count.");
                    foreach (var value in row)
                    {
                        if (value is not LiteralExpr literal)
                            throw new SqlCompilationException("INSERT VALUES currently accepts canonical literal values only.");
                        CoreProviderCapabilityRules.ValidateLiteral(literal, provider);
                    }
                }
                return;

            case InsertQuerySource querySource:
                var sourceWidth = ProjectionWidth(querySource.Query);
                if (sourceWidth is null)
                {
                    throw new SqlCompilationException(
                        "INSERT ... SELECT requires a statically known source projection width; " +
                        "wildcard projections are rejected at the Core validation boundary.");
                }
                if (sourceWidth.Value != insert.Columns.Length)
                {
                    throw new SqlCompilationException(
                        $"INSERT ... SELECT projection width {sourceWidth.Value} does not match " +
                        $"target column count {insert.Columns.Length}.");
                }
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported INSERT source during shape validation: {insert.Source.GetType().Name}");
        }
    }

    private static int? ProjectionWidth(SqlStatement statement) => statement switch
    {
        SelectStatement select when select.Select.Any(item => IsProjectionWildcard(item.Expression)) => null,
        SelectStatement select => select.Select.Length,
        QueryStatement query => ProjectionWidth(query.Head),
        _ => null
    };

    private static bool IsProjectionWildcard(SqlExpr expression) => expression switch
    {
        ColumnExpr column => IsWildcard(column.Name),
        BoundColumnExpr column => IsWildcard(column.Name),
        _ => false
    };

    private static bool IsWildcard(SqlIdentifier identifier) =>
        !identifier.Parts.IsDefaultOrEmpty
        && identifier.Parts[^1].Value == "*"
        && !identifier.Parts[^1].WasQuoted;

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
