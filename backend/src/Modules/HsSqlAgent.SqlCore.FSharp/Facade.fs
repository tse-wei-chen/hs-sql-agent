namespace HsSqlAgent.SqlCore

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Internal
open HsSqlAgent.SqlCore.Rewrite

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
                Unchecked.defaultof<string>,
                Unchecked.defaultof<string>,
                noDiagnostics)
        with ex ->
            SqlCoreTryResult<'T>(
                false,
                Unchecked.defaultof<'T>,
                codeFor ex,
                ex.Message,
                noDiagnostics)

/// C#-oriented facade for the parser and compiler pipeline.
[<AbstractClass; Sealed>]
type SqlCoreFacade private () =

    static member ParseQuery(sql: string, sourceDialect: SqlAgentToolType) : ParsedStatement =
        let parsed = FunctionalSqlTextParser.parseQuery sql sourceDialect null
        FunctionalAst.verify parsed.Statement |> ignore
        parsed

    static member ParseQuery(sql: string, sourceDialect: SqlAgentToolType, sourceProfile: SqlProviderCapabilityProfile) : ParsedStatement =
        let parsed = FunctionalSqlTextParser.parseQuery sql sourceDialect sourceProfile
        FunctionalAst.verify parsed.Statement |> ignore
        parsed

    static member ParseDml(sql: string, sourceDialect: SqlAgentToolType) : ParsedStatement =
        let parsed = FunctionalSqlTextParser.parseDml sql sourceDialect null
        FunctionalAst.verify parsed.Statement |> ignore
        parsed

    static member ParseDml(sql: string, sourceDialect: SqlAgentToolType, sourceProfile: SqlProviderCapabilityProfile) : ParsedStatement =
        let parsed = FunctionalSqlTextParser.parseDml sql sourceDialect sourceProfile
        FunctionalAst.verify parsed.Statement |> ignore
        parsed

    static member CompileQuery(parsed: ParsedStatement, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext, executionPolicy: SqlExecutionPlanPolicy) : CompiledSqlCommand =
        FunctionalAst.verify parsed.Statement |> ignore
        FunctionalPipeline.compileQuery parsed targetProvider validationContext executionPolicy null

    static member CompileQuery(parsed: ParsedStatement, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext, executionPolicy: SqlExecutionPlanPolicy, targetProfile: SqlProviderCapabilityProfile | null) : CompiledSqlCommand =
        FunctionalAst.verify parsed.Statement |> ignore
        FunctionalPipeline.compileQuery parsed targetProvider validationContext executionPolicy targetProfile

    static member CompileQuery(sql: string, sourceDialect: SqlAgentToolType, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext, executionPolicy: SqlExecutionPlanPolicy) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        sourceDialect |> ignore
        RewriteFacadeAdapter.compileQuery sql targetProvider validationContext.PolicyVersion executionPolicy.QueryMaxRows

    static member CompileQuery(sql: string, sourceDialect: SqlAgentToolType, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext, executionPolicy: SqlExecutionPlanPolicy, sourceProfile: SqlProviderCapabilityProfile, targetProfile: SqlProviderCapabilityProfile) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        ArgumentNullException.ThrowIfNull(sourceProfile)
        ArgumentNullException.ThrowIfNull(targetProfile)
        sourceDialect |> ignore
        RewriteFacadeAdapter.compileQuery sql targetProvider validationContext.PolicyVersion executionPolicy.QueryMaxRows

    static member CompileDml(parsed: ParsedStatement, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext) : CompiledSqlCommand =
        FunctionalAst.verify parsed.Statement |> ignore
        FunctionalPipeline.compileDml parsed targetProvider validationContext null null null

    static member CompileDml(parsed: ParsedStatement, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext, policy: DmlCompilationPolicy | null, targetProfile: SqlProviderCapabilityProfile | null, conflictTargetAssurance: DmlConflictTargetAssurance | null) : CompiledSqlCommand =
        FunctionalAst.verify parsed.Statement |> ignore
        FunctionalPipeline.compileDml parsed targetProvider validationContext policy targetProfile conflictTargetAssurance

    static member CompileDml(sql: string, sourceDialect: SqlAgentToolType, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        sourceDialect |> ignore
        RewriteFacadeAdapter.compileDml sql targetProvider validationContext.PolicyVersion

    static member CompileDml(sql: string, sourceDialect: SqlAgentToolType, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext, policy: DmlCompilationPolicy, sourceProfile: SqlProviderCapabilityProfile, targetProfile: SqlProviderCapabilityProfile, conflictTargetAssurance: DmlConflictTargetAssurance) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(policy)
        ArgumentNullException.ThrowIfNull(sourceProfile)
        ArgumentNullException.ThrowIfNull(targetProfile)
        ArgumentNullException.ThrowIfNull(conflictTargetAssurance)
        sourceDialect |> ignore
        RewriteFacadeAdapter.compileDml sql targetProvider validationContext.PolicyVersion

    static member TryParseQuery(sql: string, sourceDialect: SqlAgentToolType) : SqlCoreTryResult<ParsedStatement> =
        FacadeResult.capture (fun () -> SqlCoreFacade.ParseQuery(sql, sourceDialect))

    static member TryParseDml(sql: string, sourceDialect: SqlAgentToolType) : SqlCoreTryResult<ParsedStatement> =
        FacadeResult.capture (fun () -> SqlCoreFacade.ParseDml(sql, sourceDialect))

    static member TryCompileQuery(sql: string, sourceDialect: SqlAgentToolType, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext, executionPolicy: SqlExecutionPlanPolicy) : SqlCoreTryResult<CompiledSqlCommand> =
        FacadeResult.capture (fun () -> SqlCoreFacade.CompileQuery(sql, sourceDialect, targetProvider, validationContext, executionPolicy))

    static member TryCompileDml(sql: string, sourceDialect: SqlAgentToolType, targetProvider: SqlAgentToolType, validationContext: SqlPlanValidationContext) : SqlCoreTryResult<CompiledSqlCommand> =
        FacadeResult.capture (fun () -> SqlCoreFacade.CompileDml(sql, sourceDialect, targetProvider, validationContext))
