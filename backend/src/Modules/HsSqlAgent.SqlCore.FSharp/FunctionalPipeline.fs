namespace HsSqlAgent.SqlCore.Internal

open System
open HsSqlAgent.SqlCore.Core.Analysis
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Lowering
open HsSqlAgent.SqlCore.Core.Normalization
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Query compiler orchestration expressed as private F# typestates.
///
/// The legacy C# stage payloads are retained during migration because the
/// binder/normalizer/validator implementations have not moved yet. Their
/// public constructors therefore remain a compatibility surface, but the
/// migration facade no longer chains them directly: each transition below
/// accepts exactly one prior private stage and produces exactly one next stage.
///
/// Once a stage implementation moves to F#, its legacy payload can be folded
/// into the corresponding private representation without changing the facade.
module internal FunctionalPipeline =

    type private QueryContext =
        {
            Parsed: ParsedStatement
            TargetProvider: SqlAgentToolType
            TargetProfile: SqlProviderCapabilityProfile | null
        }

    type private ParsedQuery =
        | ParsedQuery of QueryContext

    type private BoundQuery =
        | BoundQuery of QueryContext * BoundStatement

    type private CanonicalQuery =
        | CanonicalQuery of QueryContext * CanonicalStatement

    type private ValidatedQuery =
        | ValidatedQuery of QueryContext * ValidatedSqlPlan

    type private ExecutableQuery =
        | ExecutableQuery of QueryContext * ExecutableSqlPlan

    let private ensureQueryRoot (statement: SqlStatement) =
        match statement with
        | :? SelectStatement
        | :? QueryStatement ->
            ()
        | other ->
            raise (SqlCompilationException(
                $"F# query pipeline requires SELECT/query-set AST, not {other.GetType().Name}."))

    let private startQuery
        (parsed: ParsedStatement)
        (targetProvider: SqlAgentToolType)
        (targetProfile: SqlProviderCapabilityProfile | null) =

        CoreProviderProfileRewriter.ValidateProfile(targetProvider, targetProfile)
        CoreSourceProfileRewriter.ValidateProfile(parsed.SourceDialect, parsed.SourceProfile)
        ensureQueryRoot parsed.Statement
        FunctionalAst.verify parsed.Statement |> ignore

        ParsedQuery
            {
                Parsed = parsed
                TargetProvider = targetProvider
                TargetProfile = targetProfile
            }

    let private bindQuery (ParsedQuery context) =
        let bound = FunctionalQueryBinder.bind context.Parsed

        CoreJoinProfileValidator.Validate(
            bound.Statement,
            context.Parsed.EnforceSourceDialectSyntax,
            bound.SourceDialect,
            context.Parsed.SourceProfile,
            context.TargetProvider,
            context.TargetProfile)

        CoreAggregateLocalOrderingGuard.Validate(
            bound.Statement,
            context.Parsed.EnforceSourceDialectSyntax,
            bound.SourceDialect,
            context.Parsed.SourceProfile,
            context.TargetProvider,
            context.TargetProfile)

        let sourcePrepared =
            if context.Parsed.EnforceSourceDialectSyntax then
                CoreSourceDialectValidator.Validate(
                    bound.Statement,
                    bound.SourceDialect)

                BoundStatement(
                    CoreSourceProfileRewriter.Prepare(
                        bound.Statement,
                        bound.SourceDialect,
                        context.Parsed.SourceProfile),
                    bound.Facts,
                    bound.SourceDialect)
            else
                bound

        CoreAggregateFilterProfileValidator.Validate(
            sourcePrepared.Statement,
            context.Parsed.EnforceSourceDialectSyntax,
            sourcePrepared.SourceDialect,
            context.Parsed.SourceProfile,
            context.TargetProvider,
            context.TargetProfile)

        FunctionalAst.verify sourcePrepared.Statement |> ignore
        BoundQuery(context, sourcePrepared)

    let private normalizeQuery (BoundQuery(context, bound)) =
        let normalized =
            CoreSqlNormalizer
                .CreateDefault()
                .Normalize(bound, context.TargetProvider)

        let sourceRestored =
            if context.Parsed.EnforceSourceDialectSyntax then
                CanonicalStatement(
                    CoreSourceProfileRewriter.Restore(normalized.Statement),
                    normalized.Facts,
                    normalized.SourceDialect,
                    normalized.TargetProvider)
            else
                normalized

        let nullOrderingCanonical =
            CanonicalStatement(
                CoreNullOrderingRewriter.Rewrite(
                    sourceRestored.Statement,
                    context.TargetProvider),
                sourceRestored.Facts,
                sourceRestored.SourceDialect,
                sourceRestored.TargetProvider)

        CoreNoFromReferenceValidator.Validate(
            nullOrderingCanonical.Statement,
            context.TargetProvider)

        FunctionalAst.verify nullOrderingCanonical.Statement |> ignore
        CanonicalQuery(context, nullOrderingCanonical)

    let private validateQuery
        (validationContext: SqlPlanValidationContext)
        (CanonicalQuery(context, canonical)) =

        let validated =
            CoreSqlPlanValidator().Validate(
                canonical,
                validationContext)

        FunctionalAst.verify validated.Statement |> ignore
        ValidatedQuery(context, validated)

    let private authorizeExecution
        (executionPolicy: SqlExecutionPlanPolicy)
        (ValidatedQuery(context, validated)) =

        let policyApplied =
            CoreSqlExecutionPolicyRewriter().Rewrite(
                validated,
                executionPolicy)

        let profiled =
            CoreProviderProfileRewriter.Rewrite(
                policyApplied.Statement,
                context.TargetProvider,
                context.TargetProfile)

        let scoped =
            CoreRootCteSetTailRewriter.Rewrite(profiled)

        let executable =
            ExecutableSqlPlan(
                scoped,
                policyApplied.Facts,
                policyApplied.SourceDialect,
                policyApplied.TargetProvider,
                policyApplied.PolicyVersion)

        CoreNativeBackendCompatibility.ValidateQuery(
            executable.Statement,
            context.TargetProvider)

        FunctionalAst.verify executable.Statement |> ignore
        ExecutableQuery(context, executable)

    let private lowerQuery (ExecutableQuery(context, executable)) =
        NativeSqlRenderer(
            context.TargetProvider,
            context.TargetProfile)
            .Lower(executable)

    type private DmlContext =
        {
            Parsed: ParsedStatement
            TargetProvider: SqlAgentToolType
            TargetProfile: SqlProviderCapabilityProfile | null
            ConflictTargetAssurance: DmlConflictTargetAssurance | null
        }

    type private ParsedDml =
        | ParsedDml of DmlContext

    type private BoundDml =
        | BoundDml of DmlContext * BoundStatement

    type private CanonicalDml =
        | CanonicalDml of DmlContext * CanonicalStatement

    type private ValidatedDml =
        | ValidatedDml of DmlContext * ValidatedSqlPlan

    type private ExecutableDml =
        | ExecutableDml of DmlContext * ExecutableSqlPlan

    let private ensureDmlRoot (statement: SqlStatement) =
        match statement with
        | :? InsertStatement
        | :? UpdateStatement
        | :? DeleteStatement ->
            ()
        | other ->
            raise (SqlCompilationException(
                $"F# DML pipeline requires INSERT/UPDATE/DELETE AST, not {other.GetType().Name}."))

    let private validateMutationPolicy
        (statement: SqlStatement)
        (policy: DmlCompilationPolicy) =

        match statement with
        | :? UpdateStatement as update ->
            if isNull update.Predicate
               && (policy.RequireWhereForUpdate || not policy.AllowFullTableUpdate) then
                raise (UnauthorizedAccessException(
                    "Security policy denies UPDATE without WHERE."))

        | :? DeleteStatement as delete ->
            if isNull delete.Predicate
               && (policy.RequireWhereForDelete || not policy.AllowFullTableDelete) then
                raise (UnauthorizedAccessException(
                    "Security policy denies DELETE without WHERE."))

        | :? InsertStatement ->
            ()

        | other ->
            raise (SqlCompilationException(
                $"Unsupported DML statement '{other.GetType().Name}'."))

    let private startDml
        (parsed: ParsedStatement)
        (targetProvider: SqlAgentToolType)
        (policy: DmlCompilationPolicy | null)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (conflictTargetAssurance: DmlConflictTargetAssurance | null) =

        let effectivePolicy =
            match policy with
            | null ->
                DmlCompilationPolicy(
                    RequireWhereForUpdate = true,
                    RequireWhereForDelete = true,
                    AllowFullTableUpdate = false,
                    AllowFullTableDelete = false)
            | nonNullPolicy ->
                nonNullPolicy

        CoreProviderProfileRewriter.ValidateProfile(targetProvider, targetProfile)
        CoreSourceProfileRewriter.ValidateProfile(parsed.SourceDialect, parsed.SourceProfile)
        ensureDmlRoot parsed.Statement
        validateMutationPolicy parsed.Statement effectivePolicy
        SqlDmlReturningExpressionCapabilityRules.ValidateSource(
            parsed.Statement,
            parsed.SourceDialect)
        FunctionalAst.verify parsed.Statement |> ignore

        ParsedDml
            {
                Parsed = parsed
                TargetProvider = targetProvider
                TargetProfile = targetProfile
                ConflictTargetAssurance = conflictTargetAssurance
            }

    let private bindDml (ParsedDml context) =
        let bound = CoreDmlBinder().Bind(context.Parsed)

        CoreJoinProfileValidator.Validate(
            bound.Statement,
            context.Parsed.EnforceSourceDialectSyntax,
            bound.SourceDialect,
            context.Parsed.SourceProfile,
            context.TargetProvider,
            context.TargetProfile)

        CoreAggregateLocalOrderingGuard.Validate(
            bound.Statement,
            context.Parsed.EnforceSourceDialectSyntax,
            bound.SourceDialect,
            context.Parsed.SourceProfile,
            context.TargetProvider,
            context.TargetProfile)

        let sourcePrepared =
            if context.Parsed.EnforceSourceDialectSyntax then
                CoreSourceDialectValidator.Validate(
                    bound.Statement,
                    bound.SourceDialect)

                BoundStatement(
                    CoreSourceProfileRewriter.Prepare(
                        bound.Statement,
                        bound.SourceDialect,
                        context.Parsed.SourceProfile),
                    bound.Facts,
                    bound.SourceDialect)
            else
                bound

        CoreAggregateFilterProfileValidator.Validate(
            sourcePrepared.Statement,
            context.Parsed.EnforceSourceDialectSyntax,
            sourcePrepared.SourceDialect,
            context.Parsed.SourceProfile,
            context.TargetProvider,
            context.TargetProfile)

        FunctionalAst.verify sourcePrepared.Statement |> ignore
        BoundDml(context, sourcePrepared)

    let private normalizeDml (BoundDml(context, bound)) =
        let normalized =
            CoreDmlNormalizer().Normalize(
                bound,
                context.TargetProvider)

        let sourceRestored =
            if context.Parsed.EnforceSourceDialectSyntax then
                CanonicalStatement(
                    CoreSourceProfileRewriter.Restore(normalized.Statement),
                    normalized.Facts,
                    normalized.SourceDialect,
                    normalized.TargetProvider)
            else
                normalized

        let nullOrderingCanonical =
            CanonicalStatement(
                CoreNullOrderingRewriter.Rewrite(
                    sourceRestored.Statement,
                    context.TargetProvider),
                sourceRestored.Facts,
                sourceRestored.SourceDialect,
                sourceRestored.TargetProvider)

        CoreNoFromReferenceValidator.Validate(
            nullOrderingCanonical.Statement,
            context.TargetProvider)

        FunctionalAst.verify nullOrderingCanonical.Statement |> ignore
        CanonicalDml(context, nullOrderingCanonical)

    let private validateDml
        (validationContext: SqlPlanValidationContext)
        (CanonicalDml(context, canonical)) =

        let validated =
            CoreDmlPlanValidator().Validate(
                canonical,
                validationContext)

        FunctionalAst.verify validated.Statement |> ignore
        ValidatedDml(context, validated)

    let private authorizeDml (ValidatedDml(context, validated)) =
        let profiled =
            CoreProviderProfileRewriter.Rewrite(
                validated.Statement,
                context.TargetProvider,
                context.TargetProfile)

        let executable =
            ExecutableSqlPlan(
                profiled,
                validated.Facts,
                validated.SourceDialect,
                validated.TargetProvider,
                validated.PolicyVersion)

        CoreNativeBackendCompatibility.ValidateDml(
            executable.Statement,
            context.TargetProvider)

        FunctionalAst.verify executable.Statement |> ignore
        ExecutableDml(context, executable)

    let private expectedDmlKind (statement: SqlStatement) =
        match statement with
        | :? InsertStatement -> SqlStatementKind.Insert
        | :? UpdateStatement -> SqlStatementKind.Update
        | :? DeleteStatement -> SqlStatementKind.Delete
        | other ->
            raise (SqlCompilationException(
                $"Statement '{other.GetType().Name}' is not supported by the F# DML pipeline."))

    let private lowerDml (ExecutableDml(context, executable)) =
        let lowered =
            NativeSqlRenderer(
                context.TargetProvider,
                context.TargetProfile)
                .Lower(executable)

        let expectedKind = expectedDmlKind context.Parsed.Statement
        if lowered.Kind <> expectedKind then
            raise (SqlCompilationException(
                $"F# DML lowerer produced {lowered.Kind} for expected {expectedKind} statement."))

        let conflictApplied =
            match executable.Statement with
            | :? InsertStatement as insert ->
                CoreDmlConflictSqlRewriter.Apply(
                    lowered,
                    insert,
                    context.TargetProfile,
                    context.ConflictTargetAssurance,
                    executable.PolicyVersion)
            | _ ->
                lowered

        CoreDmlReturningSqlRewriter.Apply(
            conflictApplied,
            executable.Statement,
            context.TargetProfile,
            executable.PolicyVersion)

    /// Compile a query through the private F# stage graph.
    ///
    /// There is deliberately no API that accepts BoundStatement,
    /// CanonicalStatement, ValidatedSqlPlan, or ExecutableSqlPlan directly.
    /// The only entry is ParsedStatement and the only exit is CompiledSqlCommand.
    let compileQuery
        (parsed: ParsedStatement)
        (targetProvider: SqlAgentToolType)
        (validationContext: SqlPlanValidationContext)
        (executionPolicy: SqlExecutionPlanPolicy)
        (targetProfile: SqlProviderCapabilityProfile | null)
        : CompiledSqlCommand =

        startQuery parsed targetProvider targetProfile
        |> bindQuery
        |> normalizeQuery
        |> validateQuery validationContext
        |> authorizeExecution executionPolicy
        |> lowerQuery

    /// Compile INSERT/UPDATE/DELETE through the same private stage discipline.
    let compileDml
        (parsed: ParsedStatement)
        (targetProvider: SqlAgentToolType)
        (validationContext: SqlPlanValidationContext)
        (policy: DmlCompilationPolicy | null)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (conflictTargetAssurance: DmlConflictTargetAssurance | null)
        : CompiledSqlCommand =

        startDml
            parsed
            targetProvider
            policy
            targetProfile
            conflictTargetAssurance
        |> bindDml
        |> normalizeDml
        |> validateDml validationContext
        |> authorizeDml
        |> lowerDml
