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

    /// The physical Query read-set is captured from the *bound* AST, after
    /// lexical CTE resolution. CteTable is a scoped alias, never a physical
    /// source. Derived/LATERAL tables, set branches and expression subqueries
    /// are visited by the bounded, iterative Query graph traversal.
    /// This is a compiler consistency proof, NOT a database read-only grant.
    type ReadSetProof = private ReadSetProof of Set<string>

    let private physicalTables (query: Query) =
        let reads = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let addSource = function
            | NamedTable(identifier, _) ->
                reads.Add(Identifier.text identifier) |> ignore
            | CteTable _ | DerivedTable _ | LateralDerivedTable _ -> ()
        let visitQuery (current: Query) =
            let select = current.Head
            select.From |> Option.iter addSource
            for join in select.Joins do
                match join with
                | OnJoin(_, source, _) | CrossJoin source | ApplyJoin(_, source)
                | NaturalJoin(_, source) | UsingJoin(_, source, _) -> addSource source

        // Never recursively walk a query/expression graph: large or hostile
        // CTE/Window/Derived trees must honor the same resource budget as
        // the compiler's other graph traversals.
        RewriteAstTraversal.mapQueryGraphPostOrder ignore id visitQuery id query
        |> ignore
        reads |> Set.ofSeq

    let internal proveBoundReadSet (bound: BoundSql) =
        match (Bound.value bound).Statement with
        | QueryStatement query -> ReadSetProof(physicalTables query)
        | _ -> invalidOp "Only a bound Query can establish Query read-set evidence."

    let internal verifyNoNewPhysicalReads (ReadSetProof boundReads) (document: Document) =
        match document.Statement with
        | QueryStatement query ->
            let finalReads = physicalTables query
            if not (Set.isSubset finalReads boundReads) then
                let message =
                    "Query normalization/validation introduced a physical relation that was not in its bound read-set."
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
