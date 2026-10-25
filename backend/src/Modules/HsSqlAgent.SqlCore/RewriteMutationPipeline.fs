namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy

/// Domain-owned typestates: Query and Mutation authorization cannot be interchanged.
/// The constructors are private and the only entry is a semantically validated SQL plan.
module internal RewriteMutationPipeline =

    /// A statement family is verified at every change in AST stage. The private
    /// constructors prevent the opposing family from entering later stages
    /// through an arbitrary generic ParsedSql/BoundSql/CanonicalSql value.
    type ParsedMutation = private ParsedMutation of Typestate.ParsedSql
    type BoundMutation = private BoundMutation of Typestate.BoundSql
    type CanonicalMutation = private CanonicalMutation of Typestate.CanonicalSql

    let internal fromParsed (parsed: Typestate.ParsedSql) =
        match (Typestate.Parsed.value parsed).Statement with
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ -> ParsedMutation parsed
        | QueryStatement _ ->
            invalidOp "Query statement cannot enter the mutation parsing stage."

    let internal fromBound (bound: Typestate.BoundSql) =
        match (Typestate.Bound.value bound).Statement with
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ -> BoundMutation bound
        | QueryStatement _ ->
            invalidOp "Query statement cannot enter the mutation binding stage."

    let internal bind bindSource (ParsedMutation parsed) =
        bindSource parsed |> fromBound

    let internal normalize normalizeBound (BoundMutation bound) =
        let canonical = normalizeBound bound
        match (Typestate.Canonical.value canonical).Statement with
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ -> CanonicalMutation canonical
        | QueryStatement _ ->
            invalidOp "Query statement cannot enter the mutation canonical stage."

    type ValidatedMutation = private ValidatedMutation of RewriteStages.ValidatedSql
    type AuthorizedMutation = private AuthorizedMutation of RewritePolicy.ExecutableSql

    let internal fromValidated (validated: RewriteStages.ValidatedSql) =
        match (RewriteStages.Validated.value validated).Statement with
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            ValidatedMutation validated
        | QueryStatement _ -> invalidOp "Query statement cannot enter the mutation compiler policy."

    let internal validate validateCanonical (CanonicalMutation canonical) =
        validateCanonical canonical |> fromValidated

    let internal authorize (updateSafety: MutationSafety) (deleteSafety: MutationSafety) (ValidatedMutation validated) =
        AuthorizedMutation(RewriteMutationPolicy.apply updateSafety deleteSafety validated)

    /// The renderer receives a token produced only by this family's policy.
    let internal render renderAuthorized (AuthorizedMutation executable) =
        renderAuthorized executable
