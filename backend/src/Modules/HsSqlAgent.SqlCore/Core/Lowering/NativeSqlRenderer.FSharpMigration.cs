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
    internal void ValidateJoinCapabilityForFunctional(JoinSource join)
    {
        var capabilityError = SqlJoinCapabilityRules.TargetValidationError(
            join.Kind,
            Provider,
            TargetProfile);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);
    }
}
