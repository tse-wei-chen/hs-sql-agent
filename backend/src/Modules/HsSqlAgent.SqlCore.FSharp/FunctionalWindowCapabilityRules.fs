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

            let contract = SqlCanonicalFunctionRegistry.Find(name)

            let functionError =
                match contract with
                | null -> null
                | contractValue
                    when contractValue.TargetCapabilityFamily = SqlCanonicalTargetCapabilityFamily.WindowFunction ->
                    SqlWindowCapabilityRules.FunctionValidationError(name, provider)
                | _ -> null

            if not (isNull functionError) then
                functionError
            else
                let frameInsensitiveError =
                    match contract, windowed.Window.Frame with
                    | contractValue, frame
                        when not (isNull contractValue)
                             && not (isNull frame)
                             && contractValue.IsWindowFrameInsensitive
                             && (provider = SqlAgentToolType.MsSqlServer
                                 || provider = SqlAgentToolType.Oracle) ->
                        SqlWindowCapabilityRules.CapabilityError(
                            provider,
                            "window.frame." + name.ToLowerInvariant())
                    | _ -> null

                if not (isNull frameInsensitiveError) then
                    frameInsensitiveError
                else
                    let orderByError =
                        match contract with
                        | null -> null
                        | contractValue
                            when provider = SqlAgentToolType.MsSqlServer
                                 && contractValue.Kind = SqlCanonicalFunctionKind.Window
                                 && windowed.Window.OrderBy.IsDefaultOrEmpty ->
                            SqlWindowCapabilityRules.CapabilityError(provider, "window.order_by")
                        | _ -> null

                    if not (isNull orderByError) then
                        orderByError
                    else
                        match windowed.Window.Frame with
                        | null -> null
                        | frame
                            when provider = SqlAgentToolType.MsSqlServer
                                 && frame.Unit = WindowFrameUnitKind.Range ->
                            let hasEndOffset =
                                match frame.End with
                                | null -> false
                                | endBound -> SqlWindowCapabilityRules.HasOffsetBound(endBound)

                            if SqlWindowCapabilityRules.HasOffsetBound(frame.Start) || hasEndOffset then
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
