using System.Collections.Generic;
using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Lowering;

internal enum FunctionalQueryPosition
{
    Root,
    CteDefinition,
    DerivedTable,
    SetBranch,
    ScalarSubquery
}

public sealed partial class NativeSqlRenderer
{
    internal NativeSqlFragment RenderSqlServerOffsetSelectForFunctional(
        SelectStatement statement) =>
        RenderSqlServerOffsetSelect(statement with
        {
            Ctes = ImmutableArray<CteDefinition>.Empty
        });

    internal NativeSqlFragment RenderExpressionForFunctional(
        SqlExpr expression,
        Func<SqlStatement, NativeSqlFragment> renderSubquery) =>
        NativeSqlExpressionRenderer.Render(
            expression,
            Provider,
            renderSubquery,
            dmlContext: false);

    internal NativeSqlFragment RenderPredicateForFunctional(
        SqlExpr expression,
        Func<SqlStatement, NativeSqlFragment> renderSubquery) =>
        NativeSqlExpressionRenderer.RenderPredicate(
            expression,
            Provider,
            renderSubquery,
            dmlContext: false);

    internal void SharePostgresGroupingBindingsForFunctional(
        SelectStatement statement,
        List<NativeSqlFragment> projections,
        NativeSqlFragment[] groupItems) =>
        SharePostgresGroupingBindings(statement, projections, groupItems);

    internal void ValidateJoinCapabilityForFunctional(JoinSource join)
    {
        var capabilityError = SqlJoinCapabilityRules.TargetValidationError(
            join.Kind,
            Provider,
            TargetProfile);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);
    }

    internal NativeSqlFragment RenderSetTailWrapperForFunctional(
        NativeSqlFragment inner,
        ImmutableArray<OrderByItem> orderBy,
        int? limit,
        int? offset,
        ImmutableArray<SelectItem> projection) =>
        RenderSetTailWrapper(inner, orderBy, limit, offset, projection);

    internal NativeSqlFragment RenderDirectSetTailForFunctional(
        ImmutableArray<OrderByItem> orderBy,
        int? limit,
        int? offset,
        ImmutableArray<SelectItem> projection) =>
        RenderDirectSetTail(orderBy, limit, offset, projection);
}
