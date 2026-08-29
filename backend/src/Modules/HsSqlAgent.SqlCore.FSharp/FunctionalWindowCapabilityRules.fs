namespace HsSqlAgent.SqlCore.Models

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Enums

/// F# ownership of target-provider window capability validation.
type internal SqlWindowCapabilityRules private () =

    static member private CapabilityError(provider: SqlAgentToolType, capability: string) =
        "SQL capability '" + capability + "' is not supported by provider " +
        string provider + " for this Core plan."

    static member private IdentifierText(identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    static member private HasOffsetBound(bound: WindowFrameBoundCore) =
        bound.Kind = WindowFrameBoundKindCore.Preceding
        || bound.Kind = WindowFrameBoundKindCore.Following

    static member private DirectWindowFunction(expression: SqlExpr) : FunctionCallExpr | null =
        match expression with
        | :? FunctionCallExpr as functionCall -> functionCall
        | :? FilterExpr as filter ->
            match filter.Expression with
            | :? FunctionCallExpr as functionCall -> functionCall
            | _ -> null
        | _ -> null

    static member SupportsAggregateInWindowSpecification(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.Postgres

    static member FunctionValidationError(
        functionName: string,
        provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.MsSqlServer then
            SqlWindowCapabilityRules.CapabilityError(
                provider,
                "function." + functionName.Trim().ToLowerInvariant())
        else
            null

    static member LiteralOffsetValidationError(
        functionName: string,
        offset: int64,
        provider: SqlAgentToolType) : string | null =
        if offset < 0L
           && (provider = SqlAgentToolType.MsSqlServer
               || provider = SqlAgentToolType.MySQL) then
            SqlWindowCapabilityRules.CapabilityError(
                provider,
                "function." + functionName.Trim().ToLowerInvariant() + ".negative_offset")
        else
            null

    static member WindowValidationError(
        windowed: WindowedExpr,
        provider: SqlAgentToolType) : string | null =
        ArgumentNullException.ThrowIfNull(windowed)

        match SqlWindowCapabilityRules.DirectWindowFunction(windowed.Expression) with
        | null -> null
        | functionCall ->
            let name =
                SqlWindowCapabilityRules.IdentifierText(functionCall.Name).ToUpperInvariant()

            let contract =
                SqlCanonicalFunctionRegistry.Find(name)
                |> Option.ofObj

            let functionError =
                match contract with
                | Some contractValue
                    when contractValue.TargetCapabilityFamily = SqlCanonicalTargetCapabilityFamily.WindowFunction ->
                    SqlWindowCapabilityRules.FunctionValidationError(name, provider)
                    |> Option.ofObj
                | _ -> None

            match functionError with
            | Some error -> error
            | None ->
                let frame = windowed.Window.Frame |> Option.ofObj

                let frameInsensitiveError =
                    match contract, frame with
                    | Some contractValue, Some _
                        when contractValue.IsWindowFrameInsensitive
                             && (provider = SqlAgentToolType.MsSqlServer
                                 || provider = SqlAgentToolType.Oracle) ->
                        Some(
                            SqlWindowCapabilityRules.CapabilityError(
                                provider,
                                "window.frame." + name.ToLowerInvariant()))
                    | _ -> None

                match frameInsensitiveError with
                | Some error -> error
                | None ->
                    let orderByError =
                        match contract with
                        | Some contractValue
                            when provider = SqlAgentToolType.MsSqlServer
                                 && contractValue.Kind = SqlCanonicalFunctionKind.Window
                                 && windowed.Window.OrderBy.IsDefaultOrEmpty ->
                            Some(SqlWindowCapabilityRules.CapabilityError(provider, "window.order_by"))
                        | _ -> None

                    match orderByError with
                    | Some error -> error
                    | None ->
                        match frame with
                        | Some frameValue
                            when provider = SqlAgentToolType.MsSqlServer
                                 && frameValue.Unit = WindowFrameUnitKind.Range ->
                            let hasEndOffset =
                                match frameValue.End |> Option.ofObj with
                                | Some endBound -> SqlWindowCapabilityRules.HasOffsetBound(endBound)
                                | None -> false

                            if SqlWindowCapabilityRules.HasOffsetBound(frameValue.Start) || hasEndOffset then
                                SqlWindowCapabilityRules.CapabilityError(provider, "window.range_offset")
                            else
                                null
                        | _ -> null

    static member BasicMatrixCapability(provider: SqlAgentToolType) =
        ignore provider
        SqlCapability(
            "window.basic",
            "window",
            SqlCapabilityStatus.Translated,
            "OVER with PARTITION BY and ORDER BY is represented structurally; provider-specific function/order requirements are validated before lowering.")

    static member FrameMatrixCapability(provider: SqlAgentToolType) =
        ignore provider
        SqlCapability(
            "window.frame",
            "window",
            SqlCapabilityStatus.Translated,
            "ROWS/RANGE frames are represented structurally; provider/function combinations that do not accept a frame and SQL Server RANGE offsets fail closed before lowering.")
