namespace HsSqlAgent.SqlCore.Core.Lowering

open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding

/// Portable direct-tail subset for set-operation queries.
/// F# owns the scope-preservation decision used by backend compatibility validation.
[<AbstractClass; Sealed>]
type internal CoreNativeSetTailScope private () =

    static member private IsSingleOutputName(identifier: SqlIdentifier) =
        identifier.Parts.Length = 1
        && (identifier.Parts[0].WasQuoted || identifier.Parts[0].Value <> "*")

    static member private IsPortableSetOutputReference(expression: SqlExpr) =
        match expression with
        | :? LiteralExpr as literal ->
            match literal.Value with
            | :? OrderByOrdinalValue -> true
            | _ -> false
        | :? ColumnExpr as column -> CoreNativeSetTailScope.IsSingleOutputName(column.Name)
        | :? BoundColumnExpr as column -> CoreNativeSetTailScope.IsSingleOutputName(column.Name)
        | _ -> false

    static member private RequiresTail(statement: QueryStatement) =
        not statement.OrderBy.IsDefaultOrEmpty
        || statement.Limit.HasValue
        || (statement.Offset.HasValue && statement.Offset.Value > 0)

    static member CanRenderDirectTail(statement: QueryStatement) =
        not statement.SetOperations.IsDefaultOrEmpty
        && not statement.Head.Ctes.IsDefaultOrEmpty
        && CoreNativeSetTailScope.RequiresTail(statement)
        && (statement.OrderBy
            |> Seq.forall (fun item ->
                CoreNativeSetTailScope.IsPortableSetOutputReference(item.Expression)))
