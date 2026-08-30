namespace HsSqlAgent.SqlCore.Core.Pipeline

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite

module private LegacyCompilerProfile =

    let validateSource (parsed: ParsedStatement) =
        match parsed.SourceProfile with
        | null -> ()
        | profile when profile.Provider <> parsed.SourceDialect ->
            raise (SqlCompilationException(
                "Source capability profile declares provider "
                + string profile.Provider
                + ", but parsed SQL declares source dialect "
                + string parsed.SourceDialect
                + "."))
        | profile when profile.CompatibilityLevel.HasValue && profile.CompatibilityLevel.Value < 0 ->
            raise (SqlCompilationException("Provider compatibility level must be non-negative."))
        | _ -> ()

    let validateTarget targetProvider (targetProfile: SqlProviderCapabilityProfile | null) =
        match targetProfile with
        | null -> ()
        | profile when profile.Provider <> targetProvider ->
            raise (SqlCompilationException(
                "Target capability profile declares provider "
                + string profile.Provider
                + ", but compilation targets "
                + string targetProvider
                + "."))
        | profile when profile.CompatibilityLevel.HasValue && profile.CompatibilityLevel.Value < 0 ->
            raise (SqlCompilationException("Provider compatibility level must be non-negative."))
        | _ -> ()

[<Sealed>]
type CoreSqlCompiler private () =

    static member CreateDefault() = CoreSqlCompiler()

    member _.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(parsed)
        LegacyCompilerProfile.validateSource parsed
        RewriteFacadeAdapter.compileQueryParsedValidated
            parsed
            targetProvider
            null
            validationContext
            executionPolicy

    member _.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        targetProfile: SqlProviderCapabilityProfile | null) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(parsed)
        LegacyCompilerProfile.validateSource parsed
        LegacyCompilerProfile.validateTarget targetProvider targetProfile
        RewriteFacadeAdapter.compileQueryParsedValidated
            parsed
            targetProvider
            targetProfile
            validationContext
            executionPolicy

[<Sealed>]
type CoreDmlCompiler private () =

    static member CreateDefault() = CoreDmlCompiler()

    member private _.CompileCore(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy | null,
        targetProfile: SqlProviderCapabilityProfile | null,
        conflictTargetAssurance: DmlConflictTargetAssurance | null) : CompiledSqlCommand =
        ArgumentNullException.ThrowIfNull(parsed)
        LegacyCompilerProfile.validateSource parsed
        LegacyCompilerProfile.validateTarget targetProvider targetProfile
        RewriteFacadeAdapter.compileDmlParsedValidated
            parsed
            targetProvider
            targetProfile
            validationContext
            policy
            conflictTargetAssurance

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext) : CompiledSqlCommand =
        this.CompileCore(parsed, targetProvider, validationContext, null, null, null)

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy | null) : CompiledSqlCommand =
        this.CompileCore(parsed, targetProvider, validationContext, policy, null, null)

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        targetProfile: SqlProviderCapabilityProfile | null) : CompiledSqlCommand =
        this.CompileCore(parsed, targetProvider, validationContext, null, targetProfile, null)

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        conflictTargetAssurance: DmlConflictTargetAssurance | null) : CompiledSqlCommand =
        this.CompileCore(parsed, targetProvider, validationContext, null, null, conflictTargetAssurance)

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy | null,
        targetProfile: SqlProviderCapabilityProfile | null) : CompiledSqlCommand =
        this.CompileCore(parsed, targetProvider, validationContext, policy, targetProfile, null)

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy | null,
        conflictTargetAssurance: DmlConflictTargetAssurance | null) : CompiledSqlCommand =
        this.CompileCore(parsed, targetProvider, validationContext, policy, null, conflictTargetAssurance)

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        targetProfile: SqlProviderCapabilityProfile | null,
        conflictTargetAssurance: DmlConflictTargetAssurance | null) : CompiledSqlCommand =
        this.CompileCore(parsed, targetProvider, validationContext, null, targetProfile, conflictTargetAssurance)

    member this.Compile(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        policy: DmlCompilationPolicy | null,
        targetProfile: SqlProviderCapabilityProfile | null,
        conflictTargetAssurance: DmlConflictTargetAssurance | null) : CompiledSqlCommand =
        this.CompileCore(
            parsed,
            targetProvider,
            validationContext,
            policy,
            targetProfile,
            conflictTargetAssurance)
