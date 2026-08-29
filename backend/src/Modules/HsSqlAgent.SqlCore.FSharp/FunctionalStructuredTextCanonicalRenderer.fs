namespace HsSqlAgent.SqlCore.Core.Lowering

open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# ownership boundary for canonical JSON extraction and mutation lowering.
module internal FunctionalStructuredTextCanonicalRenderer =

    let private requireArguments (functionCall: FunctionCallExpr) count =
        if functionCall.Arguments.Length <> count then
            let name = functionCall.Name.Parts |> Seq.map (fun part -> part.Value) |> String.concat "."
            raise (SqlCompilationException(
                "Canonical function '" + name + "' requires " + string count + " argument(s)."))

    let private validateJson
        (functionCall: FunctionCallExpr)
        canonicalName
        provider =
        match SqlJsonCapabilityRules.TargetValidationError(canonicalName, provider) with
        | null -> ()
        | capabilityError -> raise (SqlCompilationException(capabilityError))

        match SqlJsonCapabilityRules.PathValidationError(functionCall, canonicalName, provider) with
        | null -> ()
        | pathError -> raise (SqlCompilationException(pathError))

    let renderJsonExtract
        (provider: SqlAgentToolType)
        (functionCall: FunctionCallExpr)
        (renderExpression: SqlExpr -> NativeSqlFragment) =

        requireArguments functionCall 2
        validateJson functionCall "CORE_JSON_EXTRACT" provider

        let value = renderExpression functionCall.Arguments[0]
        match provider with
        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Sqlite ->
            let path = renderExpression functionCall.Arguments[1]
            NativeSqlFragment(
                "JSON_EXTRACT(" + value.Sql + ", " + path.Sql + ")",
                value.Bindings.AddRange(path.Bindings))
        | SqlAgentToolType.Postgres ->
            let segments = SqlJsonCapabilityRules.PropertyPathSegments(functionCall)
            let bindings = value.Bindings.ToBuilder()
            let placeholders = ResizeArray<string>()
            for segment in segments do
                placeholders.Add(NativeSqlParameterizer.Placeholder)
                bindings.Add(segment)
            NativeSqlFragment(
                "JSONB_EXTRACT_PATH(CAST(" + value.Sql + " AS jsonb), " +
                System.String.Join(", ", placeholders) + ")",
                bindings.ToImmutable())
        | _ ->
            raise (SqlCompilationException("JSON_EXTRACT is not supported losslessly by this provider."))

    let renderJsonSet
        (provider: SqlAgentToolType)
        (functionCall: FunctionCallExpr)
        (renderExpression: SqlExpr -> NativeSqlFragment) =

        requireArguments functionCall 3
        validateJson functionCall "CORE_JSON_SET" provider

        let value = renderExpression functionCall.Arguments[0]
        let newValue = renderExpression functionCall.Arguments[2]

        match provider with
        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Sqlite ->
            let path = renderExpression functionCall.Arguments[1]
            NativeSqlFragment(
                "JSON_SET(" + value.Sql + ", " + path.Sql + ", " + newValue.Sql + ")",
                value.Bindings.AddRange(path.Bindings).AddRange(newValue.Bindings))
        | SqlAgentToolType.MsSqlServer ->
            let path = renderExpression functionCall.Arguments[1]
            NativeSqlFragment(
                "JSON_MODIFY(" + value.Sql + ", " + path.Sql + ", " + newValue.Sql + ")",
                value.Bindings.AddRange(path.Bindings).AddRange(newValue.Bindings))
        | SqlAgentToolType.Postgres ->
            let pgPath = "{" + System.String.Join(",", SqlJsonCapabilityRules.PropertyPathSegments(functionCall)) + "}"
            NativeSqlFragment(
                "JSONB_SET(CAST(" + value.Sql + " AS jsonb), CAST(" + NativeSqlParameterizer.Placeholder +
                " AS text[]), TO_JSONB(" + newValue.Sql + "))",
                value.Bindings.Add(pgPath).AddRange(newValue.Bindings))
        | _ ->
            raise (SqlCompilationException("JSON_SET is not supported by this provider."))