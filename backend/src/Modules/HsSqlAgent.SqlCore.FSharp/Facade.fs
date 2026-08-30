namespace HsSqlAgent.SqlCore

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite

[<Sealed>]
type SqlCoreTryResult<'T>(success: bool, value: 'T, errorCode: string, errorMessage: string, diagnostics: IReadOnlyList<string>) =
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

module private FacadeCompile =
    let validateProfile provider argumentName (profile: SqlProviderCapabilityProfile) =
        ArgumentNullException.ThrowIfNull(profile)
        if profile.Provider <> provider then
            invalidArg
                argumentName
                ("Capability profile declares provider "
                 + string profile.Provider
                 + ", but compilation declares "
                 + string provider
                 + ".")
        if profile.CompatibilityLevel.HasValue && profile.CompatibilityLevel.Value < 0 then
            raise (
                ArgumentOutOfRangeException(
                    argumentName,
                    profile.CompatibilityLevel.Value,
                    "Provider compatibility level must be non-negative."))

    let queryText sql sourceDialect targetProvider (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) validationContext executionPolicy =
        RewriteFacadeAdapter.compileQueryValidated
            sql
            sourceDialect
            targetProvider
            sourceProfile
            targetProfile
            validationContext
            executionPolicy

    let dmlText sql sourceDialect targetProvider (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) validationContext policy conflictTargetAssurance =
        RewriteFacadeAdapter.compileDmlValidated
            sql
            sourceDialect
            targetProvider
            sourceProfile
            targetProfile
            validationContext
            policy
            conflictTargetAssurance

    let queryParsed parsed targetProvider (targetProfile: SqlProviderCapabilityProfile | null) validationContext executionPolicy =
        RewriteFacadeAdapter.compileQueryParsedValidated
            parsed
            targetProvider
            targetProfile
            validationContext
            executionPolicy

    let dmlParsed parsed targetProvider (targetProfile: SqlProviderCapabilityProfile | null) validationContext policy conflictTargetAssurance =
        RewriteFacadeAdapter.compileDmlParsedValidated
            parsed
            targetProvider
            targetProfile
            validationContext
            policy
            conflictTargetAssurance

/// CLR-friendly compiler facade. SQL text enters the closed F# DU + typestate pipeline directly.
/// Historical ParsedStatement overloads are a one-way compatibility seam: the CLR AST is converted
/// immediately to the same closed DU and cannot bypass binding, validation, policy, or rendering.
[<AbstractClass; Sealed>]
type SqlCoreFacade private () =

    static member CompileQuery(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(parsed)
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        match parsed.SourceProfile with
        | null -> ()
        | sourceProfile -> FacadeCompile.validateProfile parsed.SourceDialect "sourceProfile" sourceProfile
        FacadeCompile.queryParsed parsed targetProvider null validationContext executionPolicy

    static member CompileQuery(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        targetProfile: SqlProviderCapabilityProfile | null) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(parsed)
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        match parsed.SourceProfile with
        | null -> ()
        | sourceProfile -> FacadeCompile.validateProfile parsed.SourceDialect "sourceProfile" sourceProfile
        match targetProfile with
        | null -> ()
        | profile -> FacadeCompile.validateProfile targetProvider "targetProfile" profile
        FacadeCompile.queryParsed parsed targetProvider targetProfile validationContext executionPolicy

    static member CompileDml(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(parsed)
        ArgumentNullException.ThrowIfNull(validationContext)
        match parsed.SourceProfile with
        | null -> ()
        | sourceProfile -> FacadeCompile.validateProfile parsed.SourceDialect "sourceProfile" sourceProfile
        FacadeCompile.dmlParsed parsed targetProvider null validationContext null null

    static member CompileDml(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy | null,
        targetProfile: SqlProviderCapabilityProfile | null,
        conflictTargetAssurance: DmlConflictTargetAssurance | null) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(parsed)
        ArgumentNullException.ThrowIfNull(validationContext)
        match parsed.SourceProfile with
        | null -> ()
        | sourceProfile -> FacadeCompile.validateProfile parsed.SourceDialect "sourceProfile" sourceProfile
        match targetProfile with
        | null -> ()
        | profile -> FacadeCompile.validateProfile targetProvider "targetProfile" profile
        FacadeCompile.dmlParsed
            parsed
            targetProvider
            targetProfile
            validationContext
            policy
            conflictTargetAssurance

    static member CompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        FacadeCompile.queryText
            sql
            sourceDialect
            targetProvider
            null
            null
            validationContext
            executionPolicy

    static member CompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        targetProfile: SqlProviderCapabilityProfile | null) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        match targetProfile with
        | null -> ()
        | profile -> FacadeCompile.validateProfile targetProvider "targetProfile" profile
        FacadeCompile.queryText
            sql
            sourceDialect
            targetProvider
            null
            targetProfile
            validationContext
            executionPolicy

    static member CompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        sourceProfile: SqlProviderCapabilityProfile,
        targetProfile: SqlProviderCapabilityProfile) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        FacadeCompile.validateProfile sourceDialect "sourceProfile" sourceProfile
        FacadeCompile.validateProfile targetProvider "targetProfile" targetProfile
        FacadeCompile.queryText
            sql
            sourceDialect
            targetProvider
            sourceProfile
            targetProfile
            validationContext
            executionPolicy

    static member CompileDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        FacadeCompile.dmlText
            sql
            sourceDialect
            targetProvider
            null
            null
            validationContext
            null
            null

    static member CompileDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy,
        sourceProfile: SqlProviderCapabilityProfile,
        targetProfile: SqlProviderCapabilityProfile,
        conflictTargetAssurance: DmlConflictTargetAssurance) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(policy)
        ArgumentNullException.ThrowIfNull(conflictTargetAssurance)
        FacadeCompile.validateProfile sourceDialect "sourceProfile" sourceProfile
        FacadeCompile.validateProfile targetProvider "targetProfile" targetProfile
        FacadeCompile.dmlText
            sql
            sourceDialect
            targetProvider
            sourceProfile
            targetProfile
            validationContext
            policy
            conflictTargetAssurance

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
