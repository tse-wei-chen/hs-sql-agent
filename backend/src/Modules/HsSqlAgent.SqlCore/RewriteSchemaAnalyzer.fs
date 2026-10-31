namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Source-dialect-aware TABLE DDL grammar. All dialects share the same
/// non-executable Catalog Delta DU, but may enter it only through their own
/// proven syntax and type gate. No blanket "ANSI" DDL fallback exists.
module internal RewriteSchemaAnalyzer =

    /// Closed column operations are parsed completely before any Catalog mutation.
    type AlterColumnAction =
        | Add of SqlCatalogColumn
        | Remove of column: string * ifExists: bool

    type DropDependencyMode =
        | Restrict
        | Cascade

    /// Each projected output name is tracked separately from the physical
    /// source column it reads. Never use display aliases as dependency proof.
    type ViewColumnProjection =
        { /// A local binding to one FROM source, never an independent relation.
          Qualifier: string option
          SourceColumn: string
          OutputColumn: string }

    /// A closed, statically provable single-source SELECT projection.
    type ViewProjection =
        | Wildcard of qualifier: string option
        | NamedColumns of columns: ViewColumnProjection list

    /// The predicate references exactly one bound physical source. Literal
    /// variants never admit expressions, functions, implicit casts, or SQL text.
    type ViewPredicateColumn =
        { Qualifier: string option
          SourceColumn: string }

    type ViewPredicateLiteral =
        | IntegerValue of int64
        | StringValue of string
        | BooleanValue of bool

    type ViewComparison =
        | Equal | NotEqual | Less | LessOrEqual | Greater | GreaterOrEqual
        | IsDistinctFrom | IsNotDistinctFrom

    type ViewWherePredicate =
        | Compare of ViewPredicateColumn * ViewComparison * ViewPredicateLiteral
        | NullTest of ViewPredicateColumn * isNotNull: bool
        | Conjunction of ViewWherePredicate * ViewWherePredicate
        | Disjunction of ViewWherePredicate * ViewWherePredicate

    /// A direct relation in one View SELECT, with only a local binding
    /// alias. Never infer a second physical source from an expression tag.
    type ViewFromSource =
        { Relation: SqlCatalogQualifiedName
          Alias: string option }

    /// Closed two-source equality JOIN semantics. RIGHT null-extends the
    /// first relation, LEFT the second, FULL both; the outer join family
    /// is typed, not represented by a flag or free-form SQL string.
    type ViewJoinKind =
        | InnerJoin | LeftOuterJoin | RightOuterJoin | FullOuterJoin

    /// Distinct closed PostgreSQL ON comparison operators. Ordinary SQL
    /// comparisons use three-valued NULL logic. DISTINCT FROM is total:
    /// NULL-safe equality / inequality must not be replaced with = / <>.
    type ViewOnComparisonOperator =
        | OrdinaryEquality
        | OrdinaryInequality
        | LessThan
        | LessThanOrEqual
        | GreaterThan
        | GreaterThanOrEqual
        | NullSafeEquality
        | NullSafeInequality

    /// Closed Boolean ON AST. Ordinary = keeps SQL's UNKNOWN NULL result,
    /// while IS NOT DISTINCT FROM uses NULL-safe equality; AND and OR
    /// retain PostgreSQL precedence and grouping rather than a flat list.
    type ViewOnBoolean =
        | OnCompare of ViewPredicateColumn * ViewOnComparisonOperator * ViewPredicateColumn
        | OnAnd of ViewOnBoolean * ViewOnBoolean
        | OnOr of ViewOnBoolean * ViewOnBoolean

    /// Preserve the previous conjunction-only proof representation for
    /// compatibility; any OR expression is stored as a closed Boolean tree.
    type ViewJoinCriteria =
        | OnEquality of (ViewPredicateColumn * ViewOnComparisonOperator * ViewPredicateColumn) list
        | OnBoolean of ViewOnBoolean
        | UsingKeys of string list

    type ViewJoinEdge =
        { Kind: ViewJoinKind
          Condition: ViewJoinCriteria }

    /// Explicit, closed semantics for CREATE [OR REPLACE] VIEW.
    type ViewCreateMode =
        | CreateOnly
        | CreateOrReplace

    /// PostgreSQL optional column-name list overrides final SELECT output
    /// names by position, independently of physical source-column references.
    type ViewOutputHeader =
        | Inferred
        | Explicit of names: string list

    type DdlOperation =
        | Create of table: SqlCatalogQualifiedName * columns: SqlCatalogColumn list *
                    constraints: SqlCatalogConstraint list * ifNotExists: bool
        /// Firebird RECREATE TABLE is one atomic replace, not a DROP
        /// followed by a separately authorized CREATE statement.
        | Recreate of table: SqlCatalogQualifiedName * columns: SqlCatalogColumn list *
                      constraints: SqlCatalogConstraint list
        /// A fully parsed direct-source SELECT projection (no opaque SQL string).
        | CreateView of view: SqlCatalogQualifiedName * sources: ViewFromSource list *
                        projection: ViewProjection * joins: ViewJoinEdge list *
                        predicate: ViewWherePredicate option *
                        mode: ViewCreateMode * outputHeader: ViewOutputHeader
        | DropView of view: SqlCatalogQualifiedName * ifExists: bool *
                      dependencyMode: DropDependencyMode
        /// One PostgreSQL statement; validates all views and their closure together.
        | DropViewBatch of views: SqlCatalogQualifiedName list * ifExists: bool *
                           dependencyMode: DropDependencyMode
        | Drop of table: SqlCatalogQualifiedName * ifExists: bool
        /// One PostgreSQL statement; validate the whole target set before mutation.
        | DropTableBatch of tables: SqlCatalogQualifiedName list * ifExists: bool *
                            dependencyMode: DropDependencyMode
        | AlterColumns of table: SqlCatalogQualifiedName * actions: AlterColumnAction list
        /// Oracle and PostgreSQL physical column rename; index/constraint
        /// lineage is checked and atomically rewritten by the Catalog analyzer.
        | RenameColumn of table: SqlCatalogQualifiedName * oldName: string * newName: string
        /// PostgreSQL / Oracle single-constraint physical identity rename.
        /// Key-backed index behavior and name scope are provider-specific.
        | RenameConstraint of table: SqlCatalogQualifiedName * oldName: string * newName: string
        | CreateIndex of table: SqlCatalogQualifiedName * name: string * columns: string list * ifNotExists: bool
        | DropIndex of name: string * table: SqlCatalogQualifiedName option * ifExists: bool
        /// Exact PostgreSQL index relation identity, never the owning table.
        | DropQualifiedIndex of identity: SqlCatalogQualifiedName * ifExists: bool
        /// One PostgreSQL statement, one atomic multi-index Catalog proof.
        | DropIndexBatch of identities: SqlCatalogQualifiedName list * ifExists: bool
        | AddConstraint of table: SqlCatalogQualifiedName * constraintInfo: SqlCatalogConstraint
        | DropConstraint of table: SqlCatalogQualifiedName * name: string *
                            ifExists: bool * dependencyMode: DropDependencyMode
        /// MySQL native ALTER: one or several named FOREIGN KEY drops, then
        /// zero or more dependent COLUMN drops in the SAME physical statement.
        /// Order is intentionally constrained and represented as closed facts.
        | DropForeignKeys of table: SqlCatalogQualifiedName *
                             names: string list * columns: string list
        /// One native MySQL ALTER, one atomic schema effect: named FK drops
        /// followed by exactly ONE explicit named secondary index addition.
        /// Physical index-name/provenance is checked after the FK graph delta;
        /// neither stage may publish a partial known Catalog update.
        | DropForeignKeysAndAddIndex of table: SqlCatalogQualifiedName *
                                        names: string list * indexName: string *
                                        indexColumns: string list
        /// MySQL physical PRIMARY key/index, not a generic named constraint.
        | DropPrimaryKey of table: SqlCatalogQualifiedName

    type DdlSyntax =
        | Recognized of DdlOperation
        | Unsupported
        | Invalid of string

    exception private Malformed of string
    exception private OutsideSubset

    type private DdlGrammar =
        { CreateIfNotExists: bool
          DropIfExists: bool
          AddColumnKeyword: bool
          AddParentheses: bool
          DropColumnKeyword: bool
          DropBareColumn: bool
          DropColumnIfExists: bool }

    let private hasVersion (profile: SqlProviderCapabilityProfile | null) major minor =
        match profile with
        | null -> false
        | value ->
            not (isNull value.ServerVersion)
            && value.ServerVersion.CompareTo(Version(major, minor)) >= 0

    let private grammar sourceProfile = function
        | SqlAgentToolType.Postgres ->
            { CreateIfNotExists = true; DropIfExists = true
              AddColumnKeyword = true; AddParentheses = false
              DropColumnKeyword = true; DropBareColumn = true
              DropColumnIfExists = true }
        | SqlAgentToolType.MySQL ->
            { CreateIfNotExists = true; DropIfExists = true
              AddColumnKeyword = true; AddParentheses = false
              DropColumnKeyword = true; DropBareColumn = true
              DropColumnIfExists = false }
        | SqlAgentToolType.Sqlite ->
            { CreateIfNotExists = true; DropIfExists = true
              AddColumnKeyword = true; AddParentheses = false
              DropColumnKeyword = hasVersion sourceProfile 3 35; DropBareColumn = false
              DropColumnIfExists = false }
        | SqlAgentToolType.MsSqlServer ->
            // DROP TABLE IF EXISTS requires SQL Server 2016 (13.0).
            { CreateIfNotExists = false; DropIfExists = hasVersion sourceProfile 13 0
              AddColumnKeyword = false; AddParentheses = false
              DropColumnKeyword = true; DropBareColumn = false
              DropColumnIfExists = false }
        | SqlAgentToolType.Oracle ->
            { CreateIfNotExists = false; DropIfExists = false
              AddColumnKeyword = false; AddParentheses = true
              DropColumnKeyword = true; DropBareColumn = false
              DropColumnIfExists = false }
        | SqlAgentToolType.Firebird ->
            // COLUMN on DROP needs a Firebird 6+ source version proof.
            { CreateIfNotExists = false; DropIfExists = false
              AddColumnKeyword = false; AddParentheses = false
              DropColumnKeyword = hasVersion sourceProfile 6 0; DropBareColumn = true
              DropColumnIfExists = false }
        | _ -> raise OutsideSubset

    /// PostgreSQL 16-18 RESERVED_KEYWORD entries from
    /// src/include/parser/kwlist.h (REL_16_STABLE, REL_17_STABLE,
    /// REL_18_STABLE). Our typed Catalog cannot retain the SQL quoting
    /// provenance for output names, so any reserved header name is outside
    /// the proven subset even when SQL quotes it.
    let private postgresReservedViewHeaderNames =
        set [
            "ALL"; 
            "ANALYSE"; 
            "ANALYZE"; 
            "AND"; 
            "ANY"; 
            "ARRAY"; 
            "AS"; 
            "ASC"; 
            "ASYMMETRIC"; 
            "BOTH"; 
            "CASE"; 
            "CAST"; 
            "CHECK"; 
            "COLLATE"; 
            "COLUMN"; 
            "CONSTRAINT"; 
            "CREATE"; 
            "CURRENT_CATALOG"; 
            "CURRENT_DATE"; 
            "CURRENT_ROLE"; 
            "CURRENT_TIME"; 
            "CURRENT_TIMESTAMP"; 
            "CURRENT_USER"; 
            "DEFAULT"; 
            "DEFERRABLE"; 
            "DESC"; 
            "DISTINCT"; 
            "DO"; 
            "ELSE"; 
            "END"; 
            "EXCEPT"; 
            "FALSE"; 
            "FETCH"; 
            "FOR"; 
            "FOREIGN"; 
            "FROM"; 
            "GRANT"; 
            "GROUP"; 
            "HAVING"; 
            "IN"; 
            "INITIALLY"; 
            "INTERSECT"; 
            "INTO"; 
            "LATERAL"; 
            "LEADING"; 
            "LIMIT"; 
            "LOCALTIME"; 
            "LOCALTIMESTAMP"; 
            "NOT"; 
            "NULL"; 
            "OFFSET"; 
            "ON"; 
            "ONLY"; 
            "OR"; 
            "ORDER"; 
            "PLACING"; 
            "PRIMARY"; 
            "REFERENCES"; 
            "RETURNING"; 
            "SELECT"; 
            "SESSION_USER"; 
            "SOME"; 
            "SYMMETRIC"; 
            "SYSTEM_USER"; 
            "TABLE"; 
            "THEN"; 
            "TO"; 
            "TRAILING"; 
            "TRUE"; 
            "UNION"; 
            "UNIQUE"; 
            "USER"; 
            "USING"; 
            "VARIADIC"; 
            "WHEN"; 
            "WHERE"; 
            "WINDOW"; 
            "WITH"
        ]

    let private tokenWord (token: Token) =
        match token.Kind with
        | Keyword value
        | Identifier(value, false) -> value.ToUpperInvariant()
        | _ -> ""

    let parseForWith sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) (tokens: Token[]) =
        let rules = grammar sourceProfile sourceDialect
        let mutable index = 0
        let current () = tokens.[min index (tokens.Length - 1)]
        let word () = tokenWord (current ())
        let accept value =
            if word () = value then
                index <- index + 1
                true
            else false
        let expect value =
            if not (accept value) then
                raise (Malformed("Expected " + value + " in TABLE DDL."))
        let symbol c =
            match (current ()).Kind with
            | Symbol value when value = c ->
                index <- index + 1
                true
            | _ -> false
        let expectSymbol c =
            if not (symbol c) then
                raise (Malformed("Expected '" + string c + "' in TABLE DDL."))
        let normalizeName (name: string) quoted =
            RewriteCatalogIdentifierProof.canonicalName sourceDialect sourceProfile name quoted
        let namePart () =
            match (current ()).Kind with
            | Identifier(name, quoted) ->
                if RewriteCatalogIdentifierProof.mode sourceDialect sourceProfile =
                       RewriteCatalogIdentifierProof.FoldAscii
                   && not (RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier name) then
                    // A quoted "a.b" is NOT a schema-qualified a.b. Do not
                    // publish a known Catalog from an ambiguous flat name.
                    raise OutsideSubset
                index <- index + 1
                normalizeName name quoted
            | _ -> raise (Malformed("Expected a SQL identifier in TABLE DDL."))
        let qualifiedName () =
            let first = namePart ()
            let parts =
                if symbol '.' then [| first; namePart () |]
                else [| first |]
            SqlCatalogQualifiedName(Array.AsReadOnly(parts))

        // One comma-list owner: keep the first item mandatory and charge
        // the existing work budget once for each subsequent item.
        let commaSeparated first next =
            let items = ResizeArray<_>()
            items.Add(first ())
            while symbol ',' do
                RewriteResourceBudget.checkpoint ()
                items.Add(next ())
            items |> Seq.toList

        let columnNames () =
            expectSymbol '('
            let names = commaSeparated namePart namePart
            expectSymbol ')'
            names

        let typeName () =
            let kind = word ()
            if kind = "" then raise (Malformed("Expected a SQL column type in TABLE DDL."))
            index <- index + 1
            let fixedTypes, sizedTypes =
                match sourceDialect with
                | SqlAgentToolType.Postgres ->
                    set ["SMALLINT"; "INTEGER"; "INT"; "BIGINT"; "TEXT"; "BOOLEAN"; "DATE"; "TIMESTAMP"],
                    set ["VARCHAR"; "CHAR"]
                | SqlAgentToolType.MySQL ->
                    set ["SMALLINT"; "INTEGER"; "INT"; "BIGINT"; "TEXT"; "BOOLEAN"; "DATE"; "DATETIME"; "TIMESTAMP"],
                    set ["VARCHAR"; "CHAR"]
                | SqlAgentToolType.Sqlite ->
                    set ["SMALLINT"; "INTEGER"; "INT"; "BIGINT"; "TEXT"; "BOOLEAN"; "DATE"; "TIMESTAMP"; "REAL"; "BLOB"; "NUMERIC"],
                    set ["VARCHAR"; "CHAR"]
                | SqlAgentToolType.MsSqlServer ->
                    set ["SMALLINT"; "INT"; "BIGINT"; "BIT"; "DATE"; "DATETIME"; "DATETIME2"],
                    set ["VARCHAR"; "NVARCHAR"; "CHAR"; "NCHAR"]
                | SqlAgentToolType.Oracle ->
                    set ["NUMBER"; "INTEGER"; "DATE"; "TIMESTAMP"; "FLOAT"],
                    set ["VARCHAR2"; "NVARCHAR2"; "CHAR"]
                | SqlAgentToolType.Firebird ->
                    let baseTypes = set ["SMALLINT"; "INTEGER"; "INT"; "BIGINT"; "DATE"; "TIMESTAMP"]
                    let fixedTypesForVersion =
                        if hasVersion sourceProfile 3 0 then Set.add "BOOLEAN" baseTypes
                        else baseTypes
                    fixedTypesForVersion, set ["VARCHAR"; "CHAR"]
                | _ -> raise OutsideSubset
            if Set.contains kind fixedTypes then kind
            elif Set.contains kind sizedTypes then
                expectSymbol '('
                let length =
                    match (current ()).Kind with
                    | IntegerLiteral number when number > 0L && number <= 1048576L ->
                        index <- index + 1
                        number
                    | _ -> raise OutsideSubset
                expectSymbol ')'
                let supportedMaximum =
                    match sourceDialect, kind with
                    | SqlAgentToolType.Postgres, _ -> 1048576L
                    | SqlAgentToolType.Oracle, _ -> 2000L
                    | SqlAgentToolType.MsSqlServer, ("NVARCHAR" | "NCHAR") -> 4000L
                    | SqlAgentToolType.MsSqlServer, _ -> 8000L
                    | _ -> 255L
                if length > supportedMaximum then raise OutsideSubset
                kind + "(" + string length + ")"
            else raise OutsideSubset

        let column () =
            let name = namePart ()
            let kind = typeName ()
            let nullable =
                if accept "NOT" then
                    expect "NULL"
                    false
                else
                    accept "NULL" |> ignore
                    true
            SqlCatalogColumn(name, kind, nullable)
        let endOfStatement () =
            match (current ()).Kind with
            | End -> ()
            | _ -> raise OutsideSubset
        let ifNotExists () =
            if accept "IF" then
                if not rules.CreateIfNotExists then raise OutsideSubset
                expect "NOT"
                expect "EXISTS"
                true
            else false
        let ifExists () =
            if accept "IF" then
                if not rules.DropIfExists then raise OutsideSubset
                expect "EXISTS"
                true
            else false
        let parseCreate recreate =
            if recreate then
                expect "RECREATE"
                if sourceDialect <> SqlAgentToolType.Firebird then raise OutsideSubset
            else expect "CREATE"
            let viewMode =
                if recreate then CreateOnly
                elif accept "OR" then
                    // Do not generalize OR REPLACE to unrelated DDL families.
                    if not (accept "REPLACE") then raise OutsideSubset
                    CreateOrReplace
                else CreateOnly
            if accept "VIEW" then
                if recreate || sourceDialect <> SqlAgentToolType.Postgres then raise OutsideSubset
                let view = qualifiedName ()
                let viewHeaderPart () =
                    match (current ()).Kind with
                    | Identifier(name, _) when
                        Set.contains (name.ToUpperInvariant())
                            postgresReservedViewHeaderNames ->
                        // Without a quoted-identifier provenance in the
                        // public Catalog, do not claim that reserved-name
                        // headers will bind correctly downstream.
                        raise OutsideSubset
                    | _ -> namePart ()
                let outputHeader =
                    if symbol '(' then
                        let names = commaSeparated viewHeaderPart viewHeaderPart
                        expectSymbol ')'
                        Explicit names
                    else Inferred
                expect "AS"
                expect "SELECT"
                // The only proven SELECT shape has one physical FROM
                // relation, optionally aliased; direct columns may be
                // qualified by its effective local binding. Never authorize
                // JOIN, predicates, expressions or SQL execution here.
                let projectionItem () =
                    let first = namePart ()
                    if symbol '.' then
                        match (current ()).Kind with
                        | Operator "*" ->
                            index <- index + 1
                            Choice1Of2 first
                        | _ ->
                            let physical = namePart ()
                            let output = if accept "AS" then namePart () else physical
                            Choice2Of2 {
                                Qualifier = Some first
                                SourceColumn = physical
                                OutputColumn = output }
                    else
                        let output = if accept "AS" then namePart () else first
                        Choice2Of2 {
                            Qualifier = None
                            SourceColumn = first
                            OutputColumn = output }
                let projection =
                    match (current ()).Kind with
                    | Operator "*" ->
                        index <- index + 1
                        Wildcard None
                    | _ ->
                        match projectionItem () with
                        | Choice1Of2 name -> Wildcard(Some name)
                        | Choice2Of2 first ->
                            let nextColumn () =
                                match projectionItem () with
                                | Choice1Of2 _ -> raise OutsideSubset
                                | Choice2Of2 next -> next
                            NamedColumns(commaSeparated (fun () -> first) nextColumn)
                // Any trailing SELECT expression or implicit column alias
                // is outside the closed proof. End-of-statement without FROM
                // is malformed; all other additional query tokens are opaque.
                if word () <> "FROM" then
                    match (current ()).Kind with
                    | End -> raise (Malformed "CREATE VIEW SELECT requires FROM.")
                    | _ -> raise OutsideSubset
                expect "FROM"
                let source = qualifiedName ()
                // PostgreSQL permits both AS and implicit FROM aliases.
                // Never absorb clause keywords as implicit aliases.
                let sourceAlias () =
                    if accept "AS" then
                        Some(namePart ())
                    else
                        match (current ()).Kind with
                        | Identifier(name, false) when
                            not (Set.contains (name.ToUpperInvariant())
                                  postgresReservedViewHeaderNames) ->
                            Some(namePart ())
                        | Identifier(_, true) -> Some(namePart ())
                        | _ -> None
                let firstSource = { Relation = source; Alias = sourceAlias () }
                let whereColumn () =
                    let first = namePart ()
                    if symbol '.' then
                        { Qualifier = Some first; SourceColumn = namePart () }
                    else
                        { Qualifier = None; SourceColumn = first }
                // Bounded left-deep FROM graph: up to four distinct sources,
                // each edge with 1..8 direct column equality conjuncts.
                // Strictly no ON OR, NATURAL, derived relations or unbounded
                // ON expression grammar; USING has separate closed criteria.
                let joinKind () =
                    if accept "LEFT" then
                        accept "OUTER" |> ignore
                        expect "JOIN"
                        Some LeftOuterJoin
                    elif accept "RIGHT" then
                        accept "OUTER" |> ignore
                        expect "JOIN"
                        Some RightOuterJoin
                    elif accept "FULL" then
                        accept "OUTER" |> ignore
                        expect "JOIN"
                        Some FullOuterJoin
                    elif accept "INNER" then
                        expect "JOIN"
                        Some InnerJoin
                    elif accept "JOIN" then Some InnerJoin
                    else None
                let sources = ResizeArray<ViewFromSource>()
                let joins = ResizeArray<ViewJoinEdge>()
                sources.Add(firstSource)
                let mutable nextJoin = joinKind ()
                while nextJoin.IsSome do
                    RewriteResourceBudget.checkpoint ()
                    if sources.Count >= 4 then raise OutsideSubset
                    let family = Option.get nextJoin
                    let relation = qualifiedName ()
                    let source = { Relation = relation; Alias = sourceAlias () }
                    // Parse a closed criteria DU. USING names are resolved
                    // against BOTH physical inputs by the semantic analyzer;
                    // they are not qualified ON expressions or raw SQL.
                    let condition =
                        if accept "USING" then
                            let keys = columnNames ()
                            if keys.Length > 8 then raise OutsideSubset
                            UsingKeys keys
                        elif accept "ON" then
                            let pair () =
                                RewriteResourceBudget.checkpoint ()
                                let first = whereColumn ()
                                let op =
                                    match (current ()).Kind with
                                    | Operator "=" ->
                                        index <- index + 1
                                        OrdinaryEquality
                                    | Operator "<>" | Operator "!=" ->
                                        index <- index + 1
                                        OrdinaryInequality
                                    | Operator "<" ->
                                        index <- index + 1
                                        LessThan
                                    | Operator "<=" ->
                                        index <- index + 1
                                        LessThanOrEqual
                                    | Operator ">" ->
                                        index <- index + 1
                                        GreaterThan
                                    | Operator ">=" ->
                                        index <- index + 1
                                        GreaterThanOrEqual
                                    | _ when accept "IS" ->
                                        let negated = accept "NOT"
                                        expect "DISTINCT"
                                        expect "FROM"
                                        if negated then NullSafeEquality
                                        else NullSafeInequality
                                    | _ -> raise OutsideSubset
                                let second = whereColumn ()
                                first, op, second
                            // SQL AND binds tighter than OR. Keep the
                            // Boolean structure rather than collecting every
                            // leaf into a conjunction (which changes rows).
                            // Every leaf remains a bounded typed comparison.
                            let mutable onNodes = 0
                            let mutable leafCount = 0
                            let checkpointOn depth =
                                RewriteResourceBudget.checkpoint ()
                                onNodes <- onNodes + 1
                                if depth > 16 || onNodes > 64 then raise OutsideSubset
                            let rec parseOnOr depth =
                                checkpointOn depth
                                let mutable left = parseOnAnd depth
                                while accept "OR" do
                                    checkpointOn depth
                                    left <- OnOr(left, parseOnAnd depth)
                                left
                            and parseOnAnd depth =
                                checkpointOn depth
                                let mutable left = parseOnAtom depth
                                while accept "AND" do
                                    checkpointOn depth
                                    left <- OnAnd(left, parseOnAtom depth)
                                left
                            and parseOnAtom depth =
                                checkpointOn depth
                                if symbol '(' then
                                    let group = parseOnOr (depth + 1)
                                    expectSymbol ')'
                                    group
                                else
                                    if leafCount >= 8 then raise OutsideSubset
                                    leafCount <- leafCount + 1
                                    let left, op, right = pair ()
                                    OnCompare(left, op, right)
                            let expression = parseOnOr 0
                            // Pure conjunctions retain the existing closed
                            // OnEquality path; only expressions containing
                            // OR need the additional Boolean DU case.
                            let rec asConjunction node =
                                match node with
                                | OnCompare(left, op, right) ->
                                    Some [left, op, right]
                                | OnAnd(left, right) ->
                                    match asConjunction left, asConjunction right with
                                    | Some a, Some b -> Some(a @ b)
                                    | _ -> None
                                | OnOr _ -> None
                            match asConjunction expression with
                            | Some comparisons -> OnEquality comparisons
                            | None -> OnBoolean expression
                        else raise OutsideSubset
                    sources.Add(source)
                    joins.Add({ Kind = family; Condition = condition })
                    nextJoin <- joinKind ()
                let sources = sources |> Seq.toList
                let joins = joins |> Seq.toList
                // Proven WHERE subset: atomic direct column comparisons,
                // IS [NOT] NULL, AND/OR with SQL precedence, grouped clauses.
                // The bounded recursive grouping + node count prevents
                // stack-amplification from adversarial View definitions.
                let mutable predicateNodes = 0
                let checkpointPredicate () =
                    RewriteResourceBudget.checkpoint ()
                    predicateNodes <- predicateNodes + 1
                    if predicateNodes > 256 then raise OutsideSubset
                let whereLiteral () =
                    match (current ()).Kind with
                    | IntegerLiteral number ->
                        index <- index + 1
                        IntegerValue number
                    | Operator "-" ->
                        index <- index + 1
                        match (current ()).Kind with
                        | IntegerLiteral number when number <> Int64.MinValue ->
                            index <- index + 1
                            IntegerValue(-number)
                        | _ -> raise OutsideSubset
                    | StringLiteral value ->
                        index <- index + 1
                        StringValue value
                    | _ when accept "TRUE" -> BooleanValue true
                    | _ when accept "FALSE" -> BooleanValue false
                    | _ -> raise OutsideSubset
                let comparison () =
                    match (current ()).Kind with
                    | Operator "=" -> index <- index + 1; Equal
                    | Operator "<>" | Operator "!=" ->
                        index <- index + 1; NotEqual
                    | Operator "<" -> index <- index + 1; Less
                    | Operator "<=" -> index <- index + 1; LessOrEqual
                    | Operator ">" -> index <- index + 1; Greater
                    | Operator ">=" -> index <- index + 1; GreaterOrEqual
                    | _ -> raise OutsideSubset
                let rec parseOr depth =
                    let mutable left = parseAnd depth
                    while accept "OR" do
                        checkpointPredicate ()
                        let right = parseAnd depth
                        left <- Disjunction(left, right)
                    left
                and parseAnd depth =
                    let mutable left = parseAtom depth
                    while accept "AND" do
                        checkpointPredicate ()
                        let right = parseAtom depth
                        left <- Conjunction(left, right)
                    left
                and parseAtom depth =
                    checkpointPredicate ()
                    if depth > 32 then raise OutsideSubset
                    if symbol '(' then
                        let grouped = parseOr (depth + 1)
                        expectSymbol ')'
                        grouped
                    else
                        let column = whereColumn ()
                        if accept "IS" then
                            let negated = accept "NOT"
                            if accept "NULL" then
                                NullTest(column, negated)
                            else
                                // PostgreSQL NULL-safe comparison is not an ordinary
                                // = or <> predicate; retain the operator in the DU.
                                expect "DISTINCT"
                                expect "FROM"
                                let literal = whereLiteral ()
                                Compare(column,
                                        (if negated then IsNotDistinctFrom else IsDistinctFrom),
                                        literal)
                        else
                            let op = comparison ()
                            let literal = whereLiteral ()
                            Compare(column, op, literal)
                let predicate =
                    if accept "WHERE" then Some(parseOr 0) else None
                endOfStatement ()
                Recognized(CreateView(view, sources, projection, joins,
                                      predicate, viewMode, outputHeader))
            elif viewMode = CreateOrReplace then
                raise OutsideSubset
            elif accept "INDEX" then
                if recreate then raise OutsideSubset
                // SQLite alone has a proven conditional index-name capability.
                let conditional =
                    if sourceDialect = SqlAgentToolType.Sqlite then ifNotExists ()
                    else false
                let indexName = namePart ()
                expect "ON"
                let table = qualifiedName ()
                // Expression/functional/partial indexes are valid SQL but
                // outside the proven physical-column-only index subset.
                match (current ()).Kind with
                | Symbol '(' -> ()
                | _ -> raise OutsideSubset
                let names =
                    expectSymbol '('
                    let physical () =
                        match (current ()).Kind with
                        | Identifier _ -> namePart ()
                        | _ -> raise OutsideSubset
                    let cols = commaSeparated physical physical
                    expectSymbol ')'
                    cols
                endOfStatement ()
                Recognized(CreateIndex(table, indexName, names, conditional))
            else
                if not (accept "TABLE") then raise OutsideSubset
                let conditional = if recreate then false else ifNotExists ()
                let table = qualifiedName ()
                expectSymbol '('
                let columns = ResizeArray<SqlCatalogColumn>()
                let constraints = ResizeArray<SqlCatalogConstraint>()
                let optionalConstraintName () =
                    if accept "CONSTRAINT" then Some(namePart ())
                    else None
                 let makeConstraint cname kind members (remote: SqlCatalogQualifiedName) remoteMembers =
                    SqlCatalogConstraint(
                        (match cname with Some value -> value | None -> null),
                        kind, Array.AsReadOnly(List.toArray members),
                        remote, Array.AsReadOnly(List.toArray remoteMembers))
                let foreign cname members =
                    expect "REFERENCES"
                    let remote = qualifiedName ()
                    // Omitted target column / implicit PK selection is deliberately
                    // not inferred: require an explicit column list.
                    let remoteMembers = columnNames ()
                    makeConstraint cname SqlCatalogConstraintKind.ForeignKey
                        members remote remoteMembers
                let tableConstraint () =
                    let cname = optionalConstraintName ()
                    if accept "PRIMARY" then
                        expect "KEY"
                        makeConstraint cname SqlCatalogConstraintKind.PrimaryKey
                            (columnNames ()) null []
                    elif accept "UNIQUE" then
                        makeConstraint cname SqlCatalogConstraintKind.Unique
                            (columnNames ()) null []
                    elif accept "FOREIGN" then
                        expect "KEY"
                        let members = columnNames ()
                        foreign cname members
                    else raise OutsideSubset
                let element () =
                    RewriteResourceBudget.checkpoint ()
                    if word () = "CHECK" || word () = "EXCLUDE" then
                        raise OutsideSubset
                    elif word () = "PRIMARY" || word () = "UNIQUE"
                         || word () = "FOREIGN" || word () = "CONSTRAINT" then
                        constraints.Add(tableConstraint ())
                    else
                        let col = column ()
                        columns.Add col
                        let cname = optionalConstraintName ()
                        if accept "PRIMARY" then
                            expect "KEY"
                            constraints.Add(
                                makeConstraint cname SqlCatalogConstraintKind.PrimaryKey
                                    [col.Name] null [])
                        elif accept "UNIQUE" then
                            constraints.Add(
                                makeConstraint cname SqlCatalogConstraintKind.Unique
                                    [col.Name] null [])
                        elif accept "REFERENCES" then
                            // REFERENCES was consumed by the inline grammar.
                            let remote = qualifiedName ()
                            let remoteColumns = columnNames ()
                            constraints.Add(
                                makeConstraint cname SqlCatalogConstraintKind.ForeignKey
                                    [col.Name] remote remoteColumns)
                        elif cname.IsSome then
                            raise OutsideSubset
                element ()
                while symbol ',' do element ()
                if not (symbol ')') then
                    match (current ()).Kind with
                    | End -> raise (Malformed("CREATE TABLE has no closing ')'."))
                    | _ -> raise OutsideSubset
                endOfStatement ()
                let members = columns |> Seq.toList
                let deps = constraints |> Seq.toList
                if members.IsEmpty then Invalid "CREATE TABLE must contain a physical column."
                elif (members |> Seq.groupBy (fun x -> x.Name)
                      |> Seq.exists (fun (_, items) -> Seq.length items > 1)) then
                    Invalid "CREATE TABLE contains duplicate column names."
                else if recreate then Recognized(Recreate(table, members, deps))
                else Recognized(Create(table, members, deps, conditional))
        let parseDrop () =
            expect "DROP"
            if accept "INDEX" then
                // PostgreSQL index DROP and SQL Server 2016+ conditional
                // DROP share the closed typed flag, not a generic SQL fallback.
                // SQL Server still requires ON <table> and a version proof;
                // only PostgreSQL permits a qualified index identity.
                let conditional =
                    if sourceDialect = SqlAgentToolType.Postgres
                       || sourceDialect = SqlAgentToolType.MsSqlServer then
                        ifExists ()
                    else false
                let indexIdentity = qualifiedName ()
                let indexIdentities = ResizeArray<SqlCatalogQualifiedName>()
                indexIdentities.Add(indexIdentity)
                while symbol ',' do
                    if sourceDialect <> SqlAgentToolType.Postgres then
                        raise OutsideSubset
                    RewriteResourceBudget.checkpoint ()
                    indexIdentities.Add(qualifiedName ())
                let target =
                    if accept "ON" then
                        match sourceDialect with
                        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer ->
                            Some(qualifiedName ())
                        | _ -> raise OutsideSubset
                    else
                        match sourceDialect with
                        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer ->
                            raise OutsideSubset
                        | _ -> None
                // RESTRICT is PostgreSQL's default dependent-object
                // policy; explicit spelling does not add CASCADE authority.
                if sourceDialect = SqlAgentToolType.Postgres then
                    accept "RESTRICT" |> ignore
                endOfStatement ()
                match sourceDialect, indexIdentities.Count, indexIdentity.Parts.Count with
                | SqlAgentToolType.Postgres, count, _ when count > 1 ->
                    Recognized(DropIndexBatch(indexIdentities |> Seq.toList, conditional))
                | SqlAgentToolType.Postgres, 1, 2 ->
                    Recognized(DropQualifiedIndex(indexIdentity, conditional))
                | _, 1, 1 ->
                    Recognized(DropIndex(indexIdentity.Parts[0], target, conditional))
                | _ -> raise OutsideSubset
            elif accept "VIEW" then
                if sourceDialect <> SqlAgentToolType.Postgres then raise OutsideSubset
                let conditional = ifExists ()
                let view = qualifiedName ()
                let names = commaSeparated (fun () -> view) qualifiedName
                let mode =
                    if accept "CASCADE" then Cascade
                    else
                        accept "RESTRICT" |> ignore
                        Restrict
                endOfStatement ()
                if List.length names > 1 then
                    Recognized(DropViewBatch(names, conditional, mode))
                else Recognized(DropView(view, conditional, mode))
            else
                if not (accept "TABLE") then raise OutsideSubset
                let conditional = ifExists ()
                let table = qualifiedName ()
                let names = ResizeArray<SqlCatalogQualifiedName>()
                names.Add(table)
                while symbol ',' do
                    if sourceDialect <> SqlAgentToolType.Postgres then
                        raise OutsideSubset
                    RewriteResourceBudget.checkpoint ()
                    names.Add(qualifiedName ())
                // All PostgreSQL CASCADE spellings use the graph-aware
                // batch analyzer, including the single-table form.
                let mode =
                    if sourceDialect = SqlAgentToolType.Postgres then
                        if accept "CASCADE" then Cascade
                        else
                            accept "RESTRICT" |> ignore
                            Restrict
                    else Restrict
                endOfStatement ()
                match sourceDialect, names.Count, mode with
                | SqlAgentToolType.Postgres, _, Cascade ->
                    Recognized(DropTableBatch(names |> Seq.toList, conditional, Cascade))
                | SqlAgentToolType.Postgres, n, Restrict when n > 1 ->
                    Recognized(DropTableBatch(names |> Seq.toList, conditional, Restrict))
                | _, 1, Restrict -> Recognized(Drop(table, conditional))
                | _ -> raise OutsideSubset
        let parseAlter () =
            expect "ALTER"
            if not (accept "TABLE") then raise OutsideSubset
            let table = qualifiedName ()
            let constraintDefinition constraintName =
                let make kind cols (target: SqlCatalogQualifiedName) targetCols =
                    SqlCatalogConstraint(
                        constraintName, kind, Array.AsReadOnly(List.toArray cols),
                        target, Array.AsReadOnly(List.toArray targetCols))
                if accept "PRIMARY" then
                    expect "KEY"
                    make SqlCatalogConstraintKind.PrimaryKey
                        (columnNames ()) null []
                elif accept "UNIQUE" then
                    make SqlCatalogConstraintKind.Unique (columnNames ()) null []
                elif accept "FOREIGN" then
                    expect "KEY"
                    let local = columnNames ()
                    expect "REFERENCES"
                    let target = qualifiedName ()
                    make SqlCatalogConstraintKind.ForeignKey local target (columnNames ())
                else raise OutsideSubset
            let constraintAction () =
                // SQLite does not support ALTER TABLE ADD/DROP CONSTRAINT.
                // MySQL DROP FOREIGN KEY requires separate source semantics.
                match sourceDialect with
                | SqlAgentToolType.Sqlite | SqlAgentToolType.MySQL ->
                    raise OutsideSubset
                | _ ->
                    if accept "ADD" then
                        expect "CONSTRAINT"
                        let name = namePart ()
                        let result = constraintDefinition name
                        endOfStatement ()
                        Recognized(AddConstraint(table, result))
                    elif accept "DROP" then
                        expect "CONSTRAINT"
                        let conditional =
                            if sourceDialect = SqlAgentToolType.Postgres
                               && accept "IF" then
                                expect "EXISTS"
                                true
                            else false
                        let name = namePart ()
                        let mode =
                            if sourceDialect = SqlAgentToolType.Postgres then
                                if accept "CASCADE" then Cascade
                                else
                                    accept "RESTRICT" |> ignore
                                    Restrict
                            else Restrict
                        endOfStatement ()
                        Recognized(DropConstraint(table, name, conditional, mode))
                    else raise OutsideSubset
            let addAction () =
                // ADD has already been consumed. Only proven nullable column
                // definitions may be added without inspecting existing rows.
                if accept "COLUMN" && not rules.AddColumnKeyword then raise OutsideSubset
                let added = column ()
                if not added.Nullable then raise OutsideSubset
                Add added
            let dropAction () =
                // DROP has already been consumed.
                let prefixed = accept "COLUMN"
                if prefixed && not rules.DropColumnKeyword then raise OutsideSubset
                if not prefixed && not rules.DropBareColumn then raise OutsideSubset
                let conditional =
                    if accept "IF" then
                        if not rules.DropColumnIfExists then raise OutsideSubset
                        expect "EXISTS"
                        true
                    else false
                Remove(namePart (), conditional)
            let parseRepeatedActions () =
                let parseOne () =
                    if accept "ADD" then addAction ()
                    elif accept "DROP" then dropAction ()
                    else raise OutsideSubset
                RewriteResourceBudget.checkpoint ()
                commaSeparated parseOne parseOne
            let mysqlForeignKeyDropAction () =
                let names = ResizeArray<string>()
                let columns = ResizeArray<string>()
                let mutable droppingColumns = false
                let mutable addedIndex: (string * string list) option = None
                let clause () =
                    RewriteResourceBudget.checkpoint ()
                    if accept "DROP" then
                        // An index addition must be the LAST ALTER member;
                        // do not reorder SQL that changes real physical DDL.
                        if addedIndex.IsSome then raise OutsideSubset
                        if accept "FOREIGN" then
                            if droppingColumns then raise OutsideSubset
                            expect "KEY"
                            names.Add(namePart ())
                        else
                            droppingColumns <- true
                            accept "COLUMN" |> ignore
                            columns.Add(namePart ())
                    elif accept "ADD" then
                        if droppingColumns || names.Count = 0
                           || addedIndex.IsSome then raise OutsideSubset
                        if not (accept "INDEX" || accept "KEY") then
                            raise OutsideSubset
                        let indexName = namePart ()
                        let members = columnNames ()
                        addedIndex <- Some(indexName, members)
                    else raise OutsideSubset
                clause ()
                while symbol ',' do clause ()
                endOfStatement ()
                if names.Count = 0 then raise OutsideSubset
                match addedIndex with
                | Some(indexName, members) ->
                    Recognized(DropForeignKeysAndAddIndex(
                        table, names |> Seq.toList, indexName, members))
                | None ->
                    Recognized(DropForeignKeys(
                        table, names |> Seq.toList, columns |> Seq.toList))
            // MySQL ALTER TABLE has a native secondary-index lifecycle
            // that is equivalent to CREATE/DROP INDEX ... ON table. Parse
            // each source spelling into the existing closed DDL union, never
            // a separate opaque ALTER or Catalog-mutation escape hatch.
            // Restrict this proof to one physical-column index operation:
            // composite ALTER, implicit/unnamed/unique/expression indexes,
            // per-index options, and other provider spellings fail-closed.
            let mysqlIndexAction =
                sourceDialect = SqlAgentToolType.MySQL
                && index + 1 < tokens.Length
                && (word () = "ADD" || word () = "DROP")
                && (tokenWord tokens.[index + 1] = "INDEX"
                    || tokenWord tokens.[index + 1] = "KEY")
            if (sourceDialect = SqlAgentToolType.Oracle
                || sourceDialect = SqlAgentToolType.Postgres)
               && word () = "RENAME" then
                expect "RENAME"
                if accept "CONSTRAINT" then
                    // Both providers share the spelling, not the physical
                    // constraint/index namespace or capability proof.
                    let oldName = namePart ()
                    expect "TO"
                    let newName = namePart ()
                    endOfStatement ()
                    Recognized(RenameConstraint(table, oldName, newName))
                else
                    // Oracle requires COLUMN. PostgreSQL permits both
                    // RENAME COLUMN old TO new and RENAME old TO new.
                    if sourceDialect = SqlAgentToolType.Oracle then
                        expect "COLUMN"
                    else
                        accept "COLUMN" |> ignore
                    let oldName = namePart ()
                    expect "TO"
                    let newName = namePart ()
                    endOfStatement ()
                    Recognized(RenameColumn(table, oldName, newName))
            elif sourceDialect = SqlAgentToolType.MySQL
                 && word () = "DROP" && index + 1 < tokens.Length
                 && tokenWord tokens.[index + 1] = "PRIMARY" then
                expect "DROP"
                expect "PRIMARY"
                expect "KEY"
                // No compound statement proof: reject other ALTER members.
                endOfStatement ()
                Recognized(DropPrimaryKey table)
            elif sourceDialect = SqlAgentToolType.MySQL
                 && word () = "DROP" && index + 1 < tokens.Length
                 && tokenWord tokens.[index + 1] = "FOREIGN" then
                mysqlForeignKeyDropAction ()
            elif sourceDialect = SqlAgentToolType.MySQL
                 && word () = "ADD" && index + 1 < tokens.Length
                 && (tokenWord tokens.[index + 1] = "CONSTRAINT"
                     || tokenWord tokens.[index + 1] = "FOREIGN") then
                // Native ALTER ADD FOREIGN KEY syntax is structurally typed
                // but does NOT prove existing child row referential integrity.
                // The semantic analyzer must remain fail-closed for such
                // data-dependent additions, even after CREATE TABLE earlier
                // in the script: DDL can implicitly commit and other writers
                // may have modified the rows in the meantime.
                expect "ADD"
                let name =
                    if accept "CONSTRAINT" then namePart ()
                    else null
                if not (accept "FOREIGN") then raise OutsideSubset
                expect "KEY"
                let localColumns = columnNames ()
                expect "REFERENCES"
                let parent = qualifiedName ()
                let remoteColumns = columnNames ()
                endOfStatement ()
                let constraintInfo =
                    SqlCatalogConstraint(
                        name, SqlCatalogConstraintKind.ForeignKey,
                        Array.AsReadOnly(List.toArray localColumns),
                        parent, Array.AsReadOnly(List.toArray remoteColumns))
                Recognized(AddConstraint(table, constraintInfo))
            elif mysqlIndexAction then
                let adding = accept "ADD"
                if not adding then expect "DROP"
                if not (accept "INDEX" || accept "KEY") then raise OutsideSubset
                let indexName = namePart ()
                if adding then
                    let columns = columnNames ()
                    endOfStatement ()
                    Recognized(CreateIndex(table, indexName, columns, false))
                else
                    endOfStatement ()
                    Recognized(DropIndex(indexName, Some table, false))
            elif word () = "ADD" && index + 1 < tokens.Length
                 && tokenWord tokens.[index + 1] = "CONSTRAINT" then
                constraintAction ()
            elif word () = "DROP" && index + 1 < tokens.Length
                 && tokenWord tokens.[index + 1] = "CONSTRAINT" then
                constraintAction ()
            else
                let actions =
                    match sourceDialect with
                    | SqlAgentToolType.Postgres | SqlAgentToolType.MySQL | SqlAgentToolType.Firebird ->
                        parseRepeatedActions ()
                    | SqlAgentToolType.MsSqlServer ->
                        if accept "ADD" then
                            // T-SQL ADD continuation is a column definition,
                            // never another ALTER subcommand.
                            let nextColumn () =
                                if word () = "DROP" || word () = "ADD" then
                                    raise OutsideSubset
                                Add(column ())
                            let actions = commaSeparated addAction nextColumn
                            if actions |> List.exists (function Add col -> not col.Nullable | _ -> false) then
                                raise OutsideSubset
                            actions
                        elif accept "DROP" then
                            expect "COLUMN"
                            let dropName () = Remove(namePart (), false)
                            commaSeparated dropName dropName
                        else raise OutsideSubset
                    | SqlAgentToolType.Oracle ->
                        if accept "ADD" then
                            if accept "COLUMN" then raise OutsideSubset
                            let parenthesized = symbol '('
                            let addColumn () = Add(column ())
                            let actions =
                                if parenthesized then
                                    let values = commaSeparated addColumn addColumn
                                    expectSymbol ')'
                                    values
                                else [addColumn ()]
                            if actions |> List.exists (function Add col -> not col.Nullable | _ -> false) then
                                raise OutsideSubset
                            actions
                        elif accept "DROP" then
                            if symbol '(' then
                                let dropName () = Remove(namePart (), false)
                                let actions = commaSeparated dropName dropName
                                expectSymbol ')'
                                actions
                            else
                                expect "COLUMN"
                                [Remove(namePart (), false)]
                        else raise OutsideSubset
                    | SqlAgentToolType.Sqlite ->
                        if accept "ADD" then [addAction ()]
                        elif accept "DROP" then [dropAction ()]
                        else raise OutsideSubset
                    | _ -> raise OutsideSubset
                endOfStatement ()
                Recognized(AlterColumns(table, actions))

        try
            match word () with
            | "CREATE" -> parseCreate false
            | "RECREATE" -> parseCreate true
            | "DROP" -> parseDrop ()
            | "ALTER" -> parseAlter ()
            | _ -> Unsupported
        with
        | Malformed message -> Invalid message
        | OutsideSubset -> Unsupported

    /// Historic caller; preserve source semantics for direct internal uses.
    let parseFor sourceDialect tokens = parseForWith sourceDialect null tokens

    let parse tokens = parseFor SqlAgentToolType.Postgres tokens
