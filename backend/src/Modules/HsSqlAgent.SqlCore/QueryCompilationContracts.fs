namespace HsSqlAgent.SqlCore.Core.Compilation

open System
open HsSqlAgent.SqlCore.Core.Binding

/// Immutable result for a parser-native query compilation that also exposes audit/inspection facts
/// derived from the exact bound document consumed by the compiler pipeline.
[<Sealed>]
type CompiledQueryWithFacts(command: CompiledSqlCommand, facts: QueryFacts) =
    do
        ArgumentNullException.ThrowIfNull(command)
        ArgumentNullException.ThrowIfNull(facts)

    member _.Command = command
    member _.Facts = facts
