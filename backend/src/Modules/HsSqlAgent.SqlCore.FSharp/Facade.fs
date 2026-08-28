namespace HsSqlAgent.SqlCore

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing

/// CLR-friendly result returned by SqlCoreFacade Try... methods.
/// The public signature intentionally contains no FSharpOption, FSharpResult,
/// F# collection, tuple, or function types.
[<Sealed>]
type SqlCoreTryResult<'T>(
    success: bool,
    value: 'T,
    errorCode: string,
    errorMessage: string,
    diagnostics: IReadOnlyList<string>) =

    member _.Success = success
    member _.Value = value
    member _.ErrorCode = errorCode
    member _.ErrorMessage = errorMessage
    member _.Diagnostics = diagnostics

module private FacadeResult =
    let private noDiagnostics : IReadOnlyList<string> =
        Array.Empty<string>() :> IReadOnlyList<string>

    let private codeFor (ex: exn) =
        match ex with
        | :? SqlParseException -> "SQL_PARSE_ERROR"
        | :? SqlCompilationException -> "SQL_COMPILATION_ERROR"
        | :? UnauthorizedAccessException -> "SQL_POLICY_DENIED"
        | :? ArgumentException -> "INVALID_ARGUMENT"
        | _ -> "SQLCORE_ERROR"

    let capture<'T> (action: unit -> 'T) : SqlCoreTryResult<'T> =
        try
            SqlCoreTryResult<'T>(
                true,
                action(),
                null,
                null,
                noDiagnostics)
        with ex ->
            SqlCoreTryResult<'T>(
                false,
                Unchecked.defaultof<'T>,
                codeFor ex,
                ex.Message,
                noDiagnostics)

/// C#-oriented facade for the parser and compiler pipeline.
///
/// This is introduced in a temporary F# migration assembly first so C# interop
/// can be locked before the SqlCore assembly itself is switched to F#.
/// The source is intended to move unchanged into HsSqlAgent.SqlCore.fsproj at
/// the final cutover.
[<AbstractClass; Sealed>]
type SqlCoreFacade private () =

    static member ParseQuery(
        sql: string,
        sourceDialect: SqlAgentToolType) : ParsedStatement =
        CoreSqlTextParser.ParseQuery(sql, sourceDialect, null)

    static member ParseQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile) : ParsedStatement =
        CoreSqlTextParser.ParseQuery(sql, sourceDialect, sourceProfile)

    static member ParseDml(
        sql: string,
        sourceDialect: SqlAgentToolType) : ParsedStatement =
        CoreSqlTextParser.ParseDml(sql, sourceDialect, null)

    static member ParseDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile) : ParsedStatement =
        CoreSqlTextParser.ParseDml(sql, sourceDialect, sourceProfile)

    static member CompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy) : CompiledSqlCommand =
        let parsed = SqlCoreFacade.ParseQuery(sql, sourceDialect)
        CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                targetProvider,
                validationContext,
                executionPolicy,
                null)

    static member CompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        sourceProfile: SqlProviderCapabilityProfile,
        targetProfile: SqlProviderCapabilityProfile) : CompiledSqlCommand =
        let parsed = SqlCoreFacade.ParseQuery(sql, sourceDialect, sourceProfile)
        CoreSqlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                targetProvider,
                validationContext,
                executionPolicy,
                targetProfile)

    static member CompileDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext) : CompiledSqlCommand =
        let parsed = SqlCoreFacade.ParseDml(sql, sourceDialect)
        CoreDmlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                targetProvider,
                validationContext,
                null,
                null,
                null)

    static member CompileDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy,
        sourceProfile: SqlProviderCapabilityProfile,
        targetProfile: SqlProviderCapabilityProfile,
        conflictTargetAssurance: DmlConflictTargetAssurance) : CompiledSqlCommand =
        let parsed = SqlCoreFacade.ParseDml(sql, sourceDialect, sourceProfile)
        CoreDmlCompiler
            .CreateDefault()
            .Compile(
                parsed,
                targetProvider,
                validationContext,
                policy,
                targetProfile,
                conflictTargetAssurance)

    static member TryParseQuery(
        sql: string,
        sourceDialect: SqlAgentToolType) : SqlCoreTryResult<ParsedStatement> =
        FacadeResult.capture (fun () ->
            SqlCoreFacade.ParseQuery(sql, sourceDialect))

    static member TryParseDml(
        sql: string,
        sourceDialect: SqlAgentToolType) : SqlCoreTryResult<ParsedStatement> =
        FacadeResult.capture (fun () ->
            SqlCoreFacade.ParseDml(sql, sourceDialect))

    static member TryCompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy) : SqlCoreTryResult<CompiledSqlCommand> =
        FacadeResult.capture (fun () ->
            SqlCoreFacade.CompileQuery(
                sql,
                sourceDialect,
                targetProvider,
                validationContext,
                executionPolicy))

    static member TryCompileDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext) : SqlCoreTryResult<CompiledSqlCommand> =
        FacadeResult.capture (fun () ->
            SqlCoreFacade.CompileDml(
                sql,
                sourceDialect,
                targetProvider,
                validationContext))
