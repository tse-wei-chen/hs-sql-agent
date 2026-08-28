namespace HsSqlAgent.SqlCore.Internal

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
        let bound = SqlAstBinder().Bind(context.Parsed)

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
