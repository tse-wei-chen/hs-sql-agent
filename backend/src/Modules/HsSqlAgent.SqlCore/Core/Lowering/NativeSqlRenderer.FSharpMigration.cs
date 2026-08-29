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
    private static QueryPosition MapFunctionalPosition(FunctionalQueryPosition position) => position switch
    {
        FunctionalQueryPosition.Root => QueryPosition.Root,
        FunctionalQueryPosition.CteDefinition => QueryPosition.CteDefinition,
        FunctionalQueryPosition.DerivedTable => QueryPosition.DerivedTable,
        FunctionalQueryPosition.SetBranch => QueryPosition.SetBranch,
        FunctionalQueryPosition.ScalarSubquery => QueryPosition.ScalarSubquery,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null)
    };

    internal NativeSqlFragment RenderSelectBodyForFunctional(
        SelectStatement statement,
        FunctionalQueryPosition position,
        bool includeTail) =>
        RenderSelectBody(
            statement,
            MapFunctionalPosition(position),
            includeTail,
            extraProjection: null);

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
