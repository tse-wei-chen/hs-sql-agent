namespace HsSqlAgent.SqlCore.Core.Lowering

open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# ownership boundary for canonical date formatting and formatted date parsing SQL lowering.
module internal FunctionalTemporalCanonicalRenderer =

    let private requireArguments (functionCall: FunctionCallExpr) count =
        if functionCall.Arguments.Length <> count then
            raise (SqlCompilationException(
                "Canonical function '" +
                (functionCall.Name.Parts |> Seq.map (fun part -> part.Value) |> String.concat ".") +
                "' requires " + string count + " argument(s)."))

    let private stringLiteralValue (expression: SqlExpr) label =
        match expression with
        | :? LiteralExpr as literal ->
            match literal.Value with
            | :? string as value -> value
            | _ -> raise (SqlCompilationException(label + " must be a string literal."))
        | _ -> raise (SqlCompilationException(label + " must be a string literal."))

    let private sharedBinding key value =
        NativeSqlFragment(
            NativeSqlParameterizer.Placeholder,
            ImmutableArray.Create<obj | null>(NativeSharedSqlBinding(key, value)))

    let renderDateFormat
        (provider: SqlAgentToolType)
        (functionCall: FunctionCallExpr)
        (renderExpression: SqlExpr -> NativeSqlFragment) =

        requireArguments functionCall 2
        match SqlTemporalFormatCapabilityRules.TargetValidationError("CORE_DATE_FORMAT", provider) with
        | null -> ()
        | capabilityError -> raise (SqlCompilationException(capabilityError))

        let value = renderExpression functionCall.Arguments[0]
        let formatValue = stringLiteralValue functionCall.Arguments[1] "date format"
        let format = sharedBinding ("date-format:" + formatValue) formatValue

        match provider with
        | SqlAgentToolType.MsSqlServer ->
            NativeSqlFragment(
                "FORMAT(" + value.Sql + ", " + format.Sql + ")",
                value.Bindings.AddRange(format.Bindings))
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.Oracle ->
            NativeSqlFragment(
                "TO_CHAR(" + value.Sql + ", " + format.Sql + ")",
                value.Bindings.AddRange(format.Bindings))
        | SqlAgentToolType.MySQL ->
            NativeSqlFragment(
                "DATE_FORMAT(" + value.Sql + ", " + format.Sql + ")",
                value.Bindings.AddRange(format.Bindings))
        | SqlAgentToolType.Sqlite ->
            NativeSqlFragment(
                "STRFTIME(" + format.Sql + ", " + value.Sql + ")",
                format.Bindings.AddRange(value.Bindings))
        | SqlAgentToolType.Firebird ->
            raise (SqlCompilationException("portable date formatting is not supported by Firebird."))
        | _ -> raise (SqlCompilationException("Unsupported date-format provider."))

    let renderDateParse
        (provider: SqlAgentToolType)
        (functionCall: FunctionCallExpr)
        (renderExpression: SqlExpr -> NativeSqlFragment) =

        requireArguments functionCall 2
        match SqlTemporalFormatCapabilityRules.TargetValidationError("CORE_DATE_PARSE", provider) with
        | null -> ()
        | capabilityError -> raise (SqlCompilationException(capabilityError))

        let value = renderExpression functionCall.Arguments[0]
        let formatValue = stringLiteralValue functionCall.Arguments[1] "date parse format"
        let format = sharedBinding ("date-parse-format:" + formatValue) formatValue

        match provider with
        | SqlAgentToolType.MySQL ->
            NativeSqlFragment(
                "DATE(STR_TO_DATE(" + value.Sql + ", " + format.Sql + "))",
                value.Bindings.AddRange(format.Bindings))
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.Oracle ->
            NativeSqlFragment(
                "TO_DATE(" + value.Sql + ", " + format.Sql + ")",
                value.Bindings.AddRange(format.Bindings))
        | _ ->
            raise (SqlCompilationException("formatted date parsing is not supported by this provider."))
