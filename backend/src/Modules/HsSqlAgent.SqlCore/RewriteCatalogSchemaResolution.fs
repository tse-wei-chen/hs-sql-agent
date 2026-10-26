namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// A caller-declared *single* PostgreSQL search path. This is a static premise,
/// not server attestation and not an authorization grant. The full PostgreSQL
/// search_path (multiple schemas, $user, pg_catalog, pg_temp) is not modeled.
module internal RewriteCatalogSchemaResolution =

    type SchemaScope =
        | Unproven
        /// PostgreSQL declared single ordinary search_path schema.
        | SingleSchema of string
        /// MySQL current database, only when identifier-folding rules
        /// are an explicit caller-declared capability premise.
        | SingleDatabase of string

    let scope dialect (profile: SqlProviderCapabilityProfile | null) =
        match dialect, profile with
        | SqlAgentToolType.Postgres, p when not (isNull p) ->
            let setting = p.GetSessionSetting("SEARCH_PATH")
            if String.IsNullOrEmpty setting || setting <> setting.Trim()
               || setting.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
               || setting = "information_schema"
               || setting <> setting.ToLowerInvariant()
               || not (RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier setting) then
                Unproven
            else SingleSchema setting
        | _ -> Unproven

    let private partName (parts: string[]) =
        SqlCatalogQualifiedName(Array.AsReadOnly parts)

    let resolve scope (name: SqlCatalogQualifiedName) =
        match scope with
        | (SingleSchema schema | SingleDatabase schema) when name.Parts.Count = 1 ->
            partName [| schema; name.Parts[0] |]
        | _ -> name

    let private unqualifiedSystemName (name: SqlCatalogQualifiedName) =
        name.Parts.Count = 1
        && name.Parts[0].StartsWith("pg_", StringComparison.OrdinalIgnoreCase)

    let isSafeReference scope (name: SqlCatalogQualifiedName) =
        match scope with
        | Unproven -> false
        | SingleSchema _ -> not (unqualifiedSystemName name)
        | SingleDatabase _ -> true

    let private samePart a b =
        String.Equals(a, b, StringComparison.Ordinal)

    /// No old unqualified identities may shadow a projected scoped name:
    /// they have no proven physical owner because an initial Catalog can be
    /// supplied by the caller. The scope also cannot be used to model any
    /// PostgreSQL implicit system-table name.
    let canProject scope (state: SqlCatalogState) =
        match scope with
        | Unproven -> false
        | SingleSchema schema
        | SingleDatabase schema ->
            not (state.Tables |> Seq.exists (fun table ->
                let name = table.QualifiedName
                (name.Parts.Count = 1)
                || (name.Parts.Count = 2
                    && samePart name.Parts[0] schema
                    && (match scope with
                        | SingleSchema _ -> name.Parts[1].StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
                        | _ -> false))))

    /// Transient aliases are an interpretation of the declared search_path.
    /// Never persist the aliases to the state or create extra Catalog Delta.
    /// The existing strict AST validators then do both name and type checking
    /// against exactly the same resolved table.
    let projectForBinding scope (state: SqlCatalogState) =
        match scope with
        | Unproven -> state
        | SingleSchema schema
        | SingleDatabase schema ->
            let aliases =
                state.Tables
                |> Seq.choose (fun table ->
                    let name = table.QualifiedName
                    if name.Parts.Count = 2 && samePart name.Parts[0] schema then
                        let alias = partName [| name.Parts[1] |]
                        Some(SqlCatalogTable(alias.DisplayName, table.Knowledge,
                             table.Columns, table.Constraints, table.Indexes, alias))
                    else None)
            SqlCatalogState(
                state.IsClosedWorld,
                Seq.append state.Tables aliases |> Seq.toArray |> Array.AsReadOnly)
