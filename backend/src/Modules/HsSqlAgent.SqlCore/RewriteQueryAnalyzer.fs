namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Query-only read relation evidence for non-executable Script analysis.
/// A proven PostgreSQL View has output columns and structured identity in
/// the Virtual Catalog, but it is NEVER a physical Mutation target table.
/// Only the strict name/type validators consume this projection.
module internal RewriteQueryAnalyzer =

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
