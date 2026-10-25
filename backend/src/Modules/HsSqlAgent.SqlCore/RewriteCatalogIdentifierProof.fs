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
        // A dotted quoted identifier and a multi-part SQL name are different
        // AST shapes. CatalogState currently stores both as a flat string:
        // forbid dots until it persists structured identifier parts.
        not (String.IsNullOrEmpty value)
        && (value |> Seq.forall (fun c ->
            (c >= 'A' && c <= 'Z')
            || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9')
            || c = '_'))

    /// Require a canonical *already normalized* input Catalog. A manually
    /// supplied closed-world Catalog with both Users/users is not a proof:
    /// Map.ofList in the AST name binder would otherwise hide a collision.
    let safeCatalog (state: SqlCatalogState) =
        let canonical (name: string) =
            isSimpleAsciiIdentifier name
            && String.Equals(name, name.ToUpperInvariant(), StringComparison.Ordinal)
        let optionalCanonical name =
            isNull name || canonical name
        let distinctCanonical (values: seq<string>) =
            let all = values |> Seq.toList
            all.Length = (all |> List.distinct).Length
        let tables = state.Tables |> Seq.toList
        distinctCanonical (tables |> Seq.map (fun table -> table.QualifiedName.Key))
        && (tables |> List.forall (fun table ->
            (table.QualifiedName.Parts |> Seq.forall canonical)
            && distinctCanonical (table.Columns |> Seq.map (fun col -> col.Name))
            && (table.Columns |> Seq.forall (fun col -> canonical col.Name))
            && (table.Constraints |> Seq.forall (fun key ->
                optionalCanonical key.Name
                && (isNull key.ReferencedIdentity ||
                    (key.ReferencedIdentity.Parts |> Seq.forall canonical))
                && (key.Columns |> Seq.forall canonical)
                && (key.ReferencedColumns |> Seq.forall canonical)))
            && (table.Indexes |> Seq.forall (fun idx ->
                canonical idx.Name
                && (idx.Columns |> Seq.forall canonical)))))

    let canProvePhysicalNames dialect profile state (sql: string) =
        match mode dialect profile with
        | Native -> true
        | FoldAscii -> isAsciiSql sql && safeCatalog state
        | Unproven -> false

    /// Multi-part names are supported only when each part independently
    /// meets the ASCII-safe canonicalization contract. The component boundary
    /// is never recovered by splitting a flattened display string.

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
