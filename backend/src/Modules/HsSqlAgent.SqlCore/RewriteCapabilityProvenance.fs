namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Single decoder for typed capability rejection provenance.
/// A source proof reaching a target path (or the inverse) is a compiler invariant violation,
/// not a normal provider capability rejection.
module internal RewriteCapabilityProvenance =

    let sourceMessage context rejection =
        match CapabilityRejection.side rejection with
        | CapabilitySide.SourceCapability ->
            CapabilityRejection.message rejection
        | CapabilitySide.TargetCapability ->
            invalidOp ("Target capability proof reached " + context + ".")

    let targetMessage context rejection =
        match CapabilityRejection.side rejection with
        | CapabilitySide.TargetCapability ->
            CapabilityRejection.message rejection
        | CapabilitySide.SourceCapability ->
            invalidOp ("Source capability proof reached " + context + ".")
