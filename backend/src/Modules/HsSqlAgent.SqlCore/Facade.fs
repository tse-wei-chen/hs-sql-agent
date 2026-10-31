namespace HsSqlAgent.SqlCore

open System
open System.Collections.Generic
open System.Threading
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Ddl

type private SqlCoreTryState<'T> =
    | CapturedSuccess of 'T
    | CapturedFailure of errorCode: string * errorMessage: string * typedDiagnostics: IReadOnlyList<SqlDiagnostic>
    | LegacyState of
        success: bool *
        value: 'T *
        errorCode: string *
        errorMessage: string *
        typedDiagnostics: IReadOnlyList<SqlDiagnostic>

[<Sealed>]
type SqlCoreTryResult<'T> private (
    state: SqlCoreTryState<'T>,
    diagnostics: IReadOnlyList<string>,
    compileEvidence: SqlCompileEvidence | null) =

    static let noTypedDiagnostics : IReadOnlyList<SqlDiagnostic> =
        Array.Empty<SqlDiagnostic>() :> IReadOnlyList<SqlDiagnostic>

    new(
        success: bool,
        value: 'T,
        errorCode: string,
        errorMessage: string,
        diagnostics: IReadOnlyList<string>,
        typedDiagnostics: IReadOnlyList<SqlDiagnostic>) =
        SqlCoreTryResult<'T>(
            LegacyState(
                success,
                value,
                errorCode,
                errorMessage,
                typedDiagnostics),
            diagnostics,
            null)

    new(
        success: bool,
        value: 'T,
        errorCode: string,
        errorMessage: string,
        diagnostics: IReadOnlyList<string>) =
        SqlCoreTryResult<'T>(
            success,
            value,
            errorCode,
            errorMessage,
            diagnostics,
            noTypedDiagnostics)

    static member internal CapturedSuccess(
        value: 'T,
        diagnostics: IReadOnlyList<string>,
        compileEvidence: SqlCompileEvidence | null) =
        SqlCoreTryResult<'T>(
            CapturedSuccess value,
            diagnostics,
            compileEvidence)

    static member internal CapturedFailure(
        errorCode: string,
        errorMessage: string,
        diagnostics: IReadOnlyList<string>,
        typedDiagnostics: IReadOnlyList<SqlDiagnostic>,
        compileEvidence: SqlCompileEvidence | null) =
        SqlCoreTryResult<'T>(
            CapturedFailure(
                errorCode,
                errorMessage,
                typedDiagnostics),
            diagnostics,
            compileEvidence)

    member _.Success =
        match state with
        | CapturedSuccess _ -> true
        | CapturedFailure _ -> false
        | LegacyState(success, _, _, _, _) -> success

    member _.Value =
        match state with
        | CapturedSuccess value -> value
        | CapturedFailure _ -> Unchecked.defaultof<'T>
        | LegacyState(_, value, _, _, _) -> value

    member _.ErrorCode =
        match state with
        | CapturedSuccess _ -> null
        | CapturedFailure(errorCode, _, _) -> errorCode
        | LegacyState(_, _, errorCode, _, _) -> errorCode

    member _.ErrorMessage =
        match state with
        | CapturedSuccess _ -> null
        | CapturedFailure(_, errorMessage, _) -> errorMessage
        | LegacyState(_, _, _, errorMessage, _) -> errorMessage

    member _.Diagnostics = diagnostics
    member _.CompileEvidence = compileEvidence

    member _.TypedDiagnostics =
        match state with
        | CapturedSuccess _ -> noTypedDiagnostics
        | CapturedFailure(_, _, typedDiagnostics) -> typedDiagnostics
        | LegacyState(_, _, _, _, typedDiagnostics) -> typedDiagnostics

module private FacadeResult =
    let private noDiagnostics : IReadOnlyList<string> =
        Array.Empty<string>() :> IReadOnlyList<string>

    let private noTypedDiagnostics : IReadOnlyList<SqlDiagnostic> =
        Array.Empty<SqlDiagnostic>() :> IReadOnlyList<SqlDiagnostic>

    let private singletonDiagnostic diagnostic : IReadOnlyList<SqlDiagnostic> =
        [| diagnostic |] :> IReadOnlyList<SqlDiagnostic>

    let private diagnosticDataKey = "HsSqlAgent.SqlCore.Diagnostic"

    let private dataDiagnostic (ex: exn) =
        match ex.Data[diagnosticDataKey] with
        | :? SqlDiagnostic as diagnostic -> Some diagnostic
        | _ -> None

    let private codeFor (ex: exn) =
        match dataDiagnostic ex with
        | Some diagnostic when diagnostic.Stage = SqlDiagnosticStage.Policy ->
            "SQL_POLICY_DENIED"
        | Some diagnostic
            when diagnostic.Stage = SqlDiagnosticStage.Binding
                 || diagnostic.Stage = SqlDiagnosticStage.SemanticValidation
                 || diagnostic.Stage = SqlDiagnosticStage.TargetCapability
                 || diagnostic.Stage = SqlDiagnosticStage.RenderingInvariant ->
            "SQL_COMPILATION_ERROR"
        | Some diagnostic
            when diagnostic.Stage = SqlDiagnosticStage.Lexical
                 || diagnostic.Stage = SqlDiagnosticStage.Parse
                 || diagnostic.Stage = SqlDiagnosticStage.SourceValidation ->
            "SQL_PARSE_ERROR"
        | _ ->
            match ex with
            | :? SqlParseException -> "SQL_PARSE_ERROR"
            | :? SqlCompilationException -> "SQL_COMPILATION_ERROR"
            | :? UnauthorizedAccessException -> "SQL_POLICY_DENIED"
            | :? ArgumentException -> "INVALID_ARGUMENT"
            | _ -> "SQLCORE_ERROR"

    let private typedDiagnosticsFor (ex: exn) =
        let directDiagnostic : SqlDiagnostic | null =
            match ex with
            | :? SqlParseException as parseError -> parseError.Diagnostic
            | :? SqlCompilationException as compilationError -> compilationError.Diagnostic
            | _ -> null
        match directDiagnostic with
        | null ->
            match dataDiagnostic ex with
            | Some diagnostic -> singletonDiagnostic diagnostic
            | None ->
                match ex with
                | :? SqlParseException ->
                    singletonDiagnostic (
                        SqlDiagnostic(
                            "SQL_PARSE_ERROR",
                            SqlDiagnosticStage.Parse,
                            SqlDiagnosticCategory.Syntax,
                            ex.Message,
                            null))
                | :? SqlCompilationException ->
                    singletonDiagnostic (
                        SqlDiagnostic(
                            "SQL_COMPILATION_ERROR",
                            SqlDiagnosticStage.SemanticValidation,
                            SqlDiagnosticCategory.Semantic,
                            ex.Message,
                            null))
                | :? UnauthorizedAccessException ->
                    singletonDiagnostic (
                        SqlDiagnostic(
                            "SQL_POLICY_DENIED",
                            SqlDiagnosticStage.Policy,
                            SqlDiagnosticCategory.Policy,
                            ex.Message,
                            null))
                | _ -> noTypedDiagnostics
        | diagnostic -> singletonDiagnostic diagnostic

    let private evidenceForValue<'T> (value: 'T) =
        match box value with
        | :? CompiledSqlCommand as command -> command.CompileEvidence
        | _ -> null

    let capture<'T> (action: unit -> 'T) : SqlCoreTryResult<'T> =
        try
            let value = action()
            SqlCoreTryResult<'T>.CapturedSuccess(
                value,
                noDiagnostics,
                evidenceForValue value)
        with
        | :? OperationCanceledException -> reraise()
        | ex ->
            SqlCoreTryResult<'T>.CapturedFailure(
                codeFor ex,
                ex.Message,
                noDiagnostics,
                typedDiagnosticsFor ex,
                SqlCompileEvidence.TryGetFromException(ex))

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

    let queryTextWithFacts sql sourceDialect targetProvider (sourceProfile: SqlProviderCapabilityProfile | null) (targetProfile: SqlProviderCapabilityProfile | null) validationContext executionPolicy =
        RewriteFacadeAdapter.compileQueryWithFactsValidated
            sql sourceDialect targetProvider sourceProfile targetProfile validationContext executionPolicy

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

    /// Immutable CLR options use the same source validation, Catalog and cancellation
    /// path as the historical overloads; they do not carry compiler authorization.
    static member AnalyzeScript(
        sql: string,
        options: SqlScriptAnalysisOptions) : SqlScriptAnalysisResult =
        ArgumentNullException.ThrowIfNull(options)
        SqlCoreFacade.AnalyzeScript(
            sql, options.SourceDialect, options.InitialCatalog,
            options.SourceProfile, options.CancellationToken)

    static member ParseScript(
        sql: string,
        options: SqlScriptParseOptions) : SqlScriptParseResult =
        ArgumentNullException.ThrowIfNull(options)
        SqlCoreFacade.ParseScript(
            sql, options.SourceDialect, options.SourceProfile,
            options.CancellationToken)

    /// Static source classification only. This never binds, authorizes, renders, or executes SQL.
    /// Static Script analysis. Never opens a connection or produces a CompiledSqlCommand.
    /// Without an initial Catalog, the world is open and all unmentioned objects
    /// have UNKNOWN existence; callers must explicitly supply KnownEmpty() to
    /// assert that an empty catalog is a proven starting point.
    static member AnalyzeScript(
        sql: string,
        sourceDialect: SqlAgentToolType) : SqlScriptAnalysisResult =
        SqlCoreFacade.AnalyzeScript(sql, sourceDialect, SqlCatalogState.Unknown(), null)

    /// Cancel static analysis cooperatively without affecting the existing no-token API.
    static member AnalyzeScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        cancellationToken: CancellationToken) : SqlScriptAnalysisResult =
        SqlCoreFacade.AnalyzeScript(
            sql, sourceDialect, SqlCatalogState.Unknown(), null, cancellationToken)

    static member AnalyzeScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        initialCatalog: SqlCatalogState) : SqlScriptAnalysisResult =
        SqlCoreFacade.AnalyzeScript(sql, sourceDialect, initialCatalog, null)

    static member AnalyzeScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        initialCatalog: SqlCatalogState,
        cancellationToken: CancellationToken) : SqlScriptAnalysisResult =
        SqlCoreFacade.AnalyzeScript(
            sql, sourceDialect, initialCatalog, null, cancellationToken)

    static member AnalyzeScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        initialCatalog: SqlCatalogState,
        sourceProfile: SqlProviderCapabilityProfile | null) : SqlScriptAnalysisResult =
        SqlCoreFacade.AnalyzeScript(
            sql, sourceDialect, initialCatalog, sourceProfile, CancellationToken.None)

    static member AnalyzeScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        initialCatalog: SqlCatalogState,
        sourceProfile: SqlProviderCapabilityProfile | null,
        cancellationToken: CancellationToken) : SqlScriptAnalysisResult =
        ArgumentNullException.ThrowIfNull(sql)
        ArgumentNullException.ThrowIfNull(initialCatalog)
        match sourceProfile with
        | null -> ()
        | profile -> FacadeCompile.validateProfile sourceDialect "sourceProfile" profile
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            RewriteScriptAnalyzer.analyze sql sourceDialect sourceProfile initialCatalog)

    static member ParseScript(
        sql: string,
        sourceDialect: SqlAgentToolType) : SqlScriptParseResult =
        SqlCoreFacade.ParseScript(sql, sourceDialect, null)

    static member ParseScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        cancellationToken: CancellationToken) : SqlScriptParseResult =
        SqlCoreFacade.ParseScript(sql, sourceDialect, null, cancellationToken)

    static member ParseScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null) : SqlScriptParseResult =
        SqlCoreFacade.ParseScript(sql, sourceDialect, sourceProfile, CancellationToken.None)

    static member ParseScript(
        sql: string,
        sourceDialect: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile | null,
        cancellationToken: CancellationToken) : SqlScriptParseResult =
        ArgumentNullException.ThrowIfNull(sql)
        match sourceProfile with
        | null -> ()
        | profile -> FacadeCompile.validateProfile sourceDialect "sourceProfile" profile
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            RewriteScriptParser.parse sql sourceDialect sourceProfile)

    /// Strictly additive, single-statement native DDL compilation ONLY.
    /// This is not physical schema validation, Host permission or approval.
    static member CompileDdl(
        sql: string,
        options: DdlCompilationOptions) : CompiledDdlPlan =
        ArgumentNullException.ThrowIfNull(sql)
        ArgumentNullException.ThrowIfNull(options)
        if not (isNull options.SourceProfile) then
            FacadeCompile.validateProfile options.SourceDialect "sourceProfile" options.SourceProfile
        if not (isNull options.TargetProfile) then
            FacadeCompile.validateProfile options.TargetDialect "targetProfile" options.TargetProfile
        RewriteResourceBudget.withCancellation options.CancellationToken (fun () ->
            RewriteDdlCompiler.compile options sql)

    /// Cancellation propagates; grammar, validation and policy denials return diagnostics.
    static member TryCompileDdl(
        sql: string,
        options: DdlCompilationOptions) : SqlCoreTryResult<CompiledDdlPlan> =
        FacadeResult.capture (fun () -> SqlCoreFacade.CompileDdl(sql, options))

    /// Closed PostgreSQL multi-DDL compilation, never an atomic execution
    /// claim or permission. The shared Script lexer owns statement splitting.
    static member CompileDdlBatch(
        sql: string,
        options: DdlCompilationOptions) : CompiledDdlBatchPlan =
        ArgumentNullException.ThrowIfNull(sql)
        ArgumentNullException.ThrowIfNull(options)
        if not (isNull options.SourceProfile) then
            FacadeCompile.validateProfile options.SourceDialect "sourceProfile" options.SourceProfile
        if not (isNull options.TargetProfile) then
            FacadeCompile.validateProfile options.TargetDialect "targetProfile" options.TargetProfile
        RewriteResourceBudget.withCancellation options.CancellationToken (fun () ->
            RewriteDdlCompiler.compileBatch options sql)

    static member TryCompileDdlBatch(
        sql: string,
        options: DdlCompilationOptions) : SqlCoreTryResult<CompiledDdlBatchPlan> =
        FacadeResult.capture (fun () -> SqlCoreFacade.CompileDdlBatch(sql, options))

    /// Read-only virtual-Catalog schema diff. A Known Catalog supplied by the
    /// caller is NEVER a live database proof and can never authorize DDL.
    static member PreviewDdl(
        sql: string,
        options: DdlCompilationOptions) : DdlSchemaPreview =
        SqlCoreFacade.PreviewDdl(sql, options, SqlCatalogState.Unknown())

    static member PreviewDdl(
        sql: string,
        options: DdlCompilationOptions,
        initialCatalog: SqlCatalogState) : DdlSchemaPreview =
        ArgumentNullException.ThrowIfNull(sql)
        ArgumentNullException.ThrowIfNull(options)
        ArgumentNullException.ThrowIfNull(initialCatalog)
        RewriteResourceBudget.withCancellation options.CancellationToken (fun () ->
            RewriteResourceBudget.withCompilation (fun () ->
                let plan = SqlCoreFacade.CompileDdl(sql, options)
                RewriteResourceBudget.withStage "DdlPreview" SqlDiagnosticStage.SemanticValidation
                    (fun () -> RewriteDdlPreview.project plan initialCatalog)))

    static member TryPreviewDdl(
        sql: string,
        options: DdlCompilationOptions) : SqlCoreTryResult<DdlSchemaPreview> =
        FacadeResult.capture (fun () -> SqlCoreFacade.PreviewDdl(sql, options))

    static member TryPreviewDdl(
        sql: string,
        options: DdlCompilationOptions,
        initialCatalog: SqlCatalogState) : SqlCoreTryResult<DdlSchemaPreview> =
        FacadeResult.capture (fun () -> SqlCoreFacade.PreviewDdl(sql, options, initialCatalog))

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

    static member CompileQueryWithFacts(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy) : CompiledQueryWithFacts =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        FacadeCompile.queryTextWithFacts sql sourceDialect targetProvider null null validationContext executionPolicy

    static member CompileQueryWithFacts(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        sourceProfile: SqlProviderCapabilityProfile | null,
        targetProfile: SqlProviderCapabilityProfile | null) : CompiledQueryWithFacts =
        ArgumentNullException.ThrowIfNull(validationContext)
        ArgumentNullException.ThrowIfNull(executionPolicy)
        match sourceProfile with
        | null -> ()
        | profile -> FacadeCompile.validateProfile sourceDialect "sourceProfile" profile
        match targetProfile with
        | null -> ()
        | profile -> FacadeCompile.validateProfile targetProvider "targetProfile" profile
        FacadeCompile.queryTextWithFacts sql sourceDialect targetProvider sourceProfile targetProfile validationContext executionPolicy

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

    /// Opt-in cancellation is scoped to this synchronous compiler attempt.
    /// The same validated F# pipeline remains the only producer of commands.
    static member CompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        cancellationToken: CancellationToken) : CompiledSqlCommand =
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            SqlCoreFacade.CompileQuery(
                sql, sourceDialect, targetProvider, validationContext, executionPolicy))

    static member CompileDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        cancellationToken: CancellationToken) : CompiledSqlCommand =
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            SqlCoreFacade.CompileDml(
                sql, sourceDialect, targetProvider, validationContext))

    static member CompileQuery(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        cancellationToken: CancellationToken) : CompiledSqlCommand =
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            SqlCoreFacade.CompileQuery(
                parsed, targetProvider, validationContext, executionPolicy))

    static member CompileDml(
        parsed: ParsedStatement,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        cancellationToken: CancellationToken) : CompiledSqlCommand =
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            SqlCoreFacade.CompileDml(
                parsed, targetProvider, validationContext))

    /// Cancellation propagates as OperationCanceledException and is never
    /// represented as a failed SQL validation or ordinary Try result.
    static member TryCompileQuery(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        executionPolicy: SqlExecutionPlanPolicy,
        cancellationToken: CancellationToken) : SqlCoreTryResult<CompiledSqlCommand> =
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            SqlCoreFacade.TryCompileQuery(
                sql, sourceDialect, targetProvider, validationContext, executionPolicy))

    static member TryCompileDml(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        validationContext: SqlPlanValidationContext,
        cancellationToken: CancellationToken) : SqlCoreTryResult<CompiledSqlCommand> =
        RewriteResourceBudget.withCancellation cancellationToken (fun () ->
            SqlCoreFacade.TryCompileDml(
                sql, sourceDialect, targetProvider, validationContext))
