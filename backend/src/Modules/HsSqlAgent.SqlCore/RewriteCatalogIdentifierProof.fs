namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Explicit, caller-declared identifier semantics for *static analysis only*.
/// A capability profile is not evidence of the live server's configuration.
/// Non-ASCII / non-simple identifiers intentionally have no equivalence proof:
/// .NET invariant casing is not a general SQL collation implementation.
module internal RewriteCatalogIdentifierProof =

    type IdentifierMode =
        | Unproven
        | Native
        | FoldAscii

    let mode dialect (profile: SqlProviderCapabilityProfile | null) =
        match dialect with
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.Oracle
        | SqlAgentToolType.Firebird
        | SqlAgentToolType.Sqlite -> Native
        | SqlAgentToolType.MySQL ->
            match profile with
            | null -> Unproven
            | p when p.Provider <> SqlAgentToolType.MySQL -> Unproven
            | p when p.GetSessionSetting("LOWER_CASE_TABLE_NAMES") = "1" -> FoldAscii
            | _ -> Unproven
        | SqlAgentToolType.MsSqlServer ->
            match profile with
            | null -> Unproven
            | p when p.Provider <> SqlAgentToolType.MsSqlServer -> Unproven
            | p ->
                // Explicit catalog identifier collation, NOT a data-column
                // collation. Do not infer behavior from an arbitrary '_CI'.
                // This narrowly modeled Latin1 accent-sensitive family is
                // used only with ASCII identifiers.
                match p.GetSessionSetting("CATALOG_COLLATION") with
                | "Latin1_General_100_CI_AS"
                | "SQL_Latin1_General_CP1_CI_AS" -> FoldAscii
                | _ -> Unproven
        | _ -> Unproven

    let isAsciiSql (value: string) =
        value |> Seq.forall (fun c -> int c <= 127)

    let isSimpleAsciiIdentifier (value: string) =
        not (String.IsNullOrEmpty value)
        && value.Split('.')
           |> Array.forall (fun part ->
               not (String.IsNullOrEmpty part)
               && (part |> Seq.forall (fun c ->
                   (c >= 'A' && c <= 'Z')
                   || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9')
                   || c = '_')))

    let safeCatalog (state: SqlCatalogState) =
        state.Tables |> Seq.forall (fun table ->
            isSimpleAsciiIdentifier table.Name
            && (table.Columns |> Seq.forall (fun col ->
                isSimpleAsciiIdentifier col.Name))
            && (table.Constraints |> Seq.forall (fun key ->
                (isNull key.Name || isSimpleAsciiIdentifier key.Name)
                && (isNull key.ReferencedTable || isSimpleAsciiIdentifier key.ReferencedTable)
                && (key.Columns |> Seq.forall isSimpleAsciiIdentifier)
                && (key.ReferencedColumns |> Seq.forall isSimpleAsciiIdentifier)))
            && (table.Indexes |> Seq.forall (fun idx ->
                isSimpleAsciiIdentifier idx.Name
                && (idx.Columns |> Seq.forall isSimpleAsciiIdentifier))))

    let canProvePhysicalNames dialect profile state (sql: string) =
        match mode dialect profile with
        | Native -> true
        | FoldAscii -> isAsciiSql sql && safeCatalog state
        | Unproven -> false

    /// Static canonical identity: MySQL lower_case_table_names=1 compares
    /// table names case-insensitively and column names are case-insensitive.
    /// This catalog uses uppercase ASCII keys, NOT server display spelling.
    let canonicalName dialect profile (name: string) quoted =
        match dialect, mode dialect profile with
        | SqlAgentToolType.MySQL, FoldAscii ->
            if isSimpleAsciiIdentifier name then name.ToUpperInvariant()
            else name
        | _ ->
            match dialect with
            | SqlAgentToolType.Postgres -> if quoted then name else name.ToLowerInvariant()
            | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird ->
                if quoted then name else name.ToUpperInvariant()
            | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
                name.ToUpperInvariant()
            | _ -> name
