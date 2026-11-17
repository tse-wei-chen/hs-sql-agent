namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open System
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Query-only read relation evidence for non-executable Script analysis.
/// A proven PostgreSQL View has output columns and structured identity in
/// the Virtual Catalog, but it is NEVER a physical Mutation target table.
/// Only the strict name/type validators consume this projection.
module internal RewriteQueryAnalyzer =

    /// Records the named relation *references* from the bound Query AST, after
    /// lexical CTE resolution. It neither verifies live objects nor expands
    /// Views/UDFs into their underlying physical reads. CteTable is a scoped
    /// alias; derived/LATERAL, set branches and expression subqueries are
    /// visited by the existing bounded, iterative Query traversal.
    /// This is a compiler consistency guard, NEVER a Host permission grant.
    type ReadSetProof = private ReadSetProof of Set<string>

    let private namedSourceRefs (query: Query) =
        let reads = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        // This existing bounded walk includes CTE definitions, derived/lateral
        // tables, set branches, scalar/correlated subqueries and window specs.
        // Do not rebuild the AST solely to collect its physical read-set.
        RewriteAstTraversal.iterIncludingQueries
            (function
             | RewriteAstTraversal.SourceNode(NamedTable(identifier, _)) ->
                 reads.Add(Identifier.text identifier) |> ignore
             | _ -> ())
            (RewriteAstTraversal.QueryNode query)
        reads |> Set.ofSeq

    let internal proveBoundReadSet (bound: BoundSql) =
        match (Bound.value bound).Statement with
        | QueryStatement query -> ReadSetProof(namedSourceRefs query)
        | _ -> invalidOp "Only a bound Query can establish Query read-set evidence."

    let internal verifyNoAddedNamedReads (ReadSetProof boundReads) (document: Document) =
        match document.Statement with
        | QueryStatement query ->
            let finalReads = namedSourceRefs query
            if not (Set.isSubset finalReads boundReads) then
                let message =
                    "Query normalization/validation introduced a named relation absent from its bound source references."
                let span =
                    if document.Span.Start < 0 || document.Span.Length < 0 then null
                    else SqlDiagnosticSpan(document.Span.Start, document.Span.Length)
                let diagnostic =
                    SqlDiagnostic("SQL_QUERY_READ_SET_DRIFT",
                        SqlDiagnosticStage.SemanticValidation,
                        SqlDiagnosticCategory.Invariant, message, span)
                raise (SqlCompilationException(message, diagnostic))
        | _ -> invalidOp "A Mutation cannot inherit Query read-set evidence."

    let readableViews (dialect: SqlAgentToolType)
                      (state: SqlCatalogState)
                      (bound: BoundSql) : seq<SqlCatalogView> =
        match (Bound.value bound).Statement with
        | QueryStatement _ when dialect = SqlAgentToolType.Postgres ->
            // Views are independently modeled immutable relations; do not
            // copy them into state.Tables or change CatalogBefore/After.
            state.Views :> seq<SqlCatalogView>
        | _ ->
            // No implicit cross-provider View capability, and no DML target
            // permissions through the Query read contract.
            Seq.empty
