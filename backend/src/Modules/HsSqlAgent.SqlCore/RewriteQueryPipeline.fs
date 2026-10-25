namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy

/// Domain-owned typestates: Query and Mutation authorization cannot be interchanged.
/// The constructors are private and the only entry is a semantically validated SQL plan.
module internal RewriteQueryPipeline =

    /// A statement family is verified at every change in AST stage. The private
    /// constructors prevent the opposing family from entering later stages
    /// through an arbitrary generic ParsedSql/BoundSql/CanonicalSql value.
    type ParsedQuery = private ParsedQuery of Typestate.ParsedSql
    type BoundQuery = private BoundQuery of Typestate.BoundSql
    type CanonicalQuery = private CanonicalQuery of Typestate.CanonicalSql

    let internal fromParsed (parsed: Typestate.ParsedSql) =
        match (Typestate.Parsed.value parsed).Statement with
        | QueryStatement _ -> ParsedQuery parsed
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            invalidOp "Mutation statement cannot enter the query parsing stage."

    let internal fromBound (bound: Typestate.BoundSql) =
        match (Typestate.Bound.value bound).Statement with
        | QueryStatement _ -> BoundQuery bound
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            invalidOp "Mutation statement cannot enter the query binding stage."

    let internal bind bindSource (ParsedQuery parsed) =
        bindSource parsed |> fromBound

    let internal normalize normalizeBound (BoundQuery bound) =
        let canonical = normalizeBound bound
        match (Typestate.Canonical.value canonical).Statement with
        | QueryStatement _ -> CanonicalQuery canonical
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            invalidOp "Mutation statement cannot enter the query canonical stage."

    type ValidatedQuery = private ValidatedQuery of RewriteStages.ValidatedSql
    type AuthorizedQuery = private AuthorizedQuery of RewritePolicy.ExecutableSql

    let internal fromValidated (validated: RewriteStages.ValidatedSql) =
        match (RewriteStages.Validated.value validated).Statement with
        | QueryStatement _ -> ValidatedQuery validated
        | _ -> invalidOp "Mutation statement cannot enter the query compiler policy."

    let internal validate validateCanonical (CanonicalQuery canonical) =
        validateCanonical canonical |> fromValidated

    let internal authorize (rowCap: RowCap) (ValidatedQuery validated) =
        AuthorizedQuery(RewriteQueryPolicy.apply rowCap validated)

    /// The renderer receives a token produced only by this family's policy.
    let internal render renderAuthorized (AuthorizedQuery executable) =
        renderAuthorized executable
