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

    type ViewWherePredicate =
        | Compare of ViewPredicateColumn * ViewComparison * ViewPredicateLiteral
        | NullTest of ViewPredicateColumn * isNotNull: bool
        | Conjunction of ViewWherePredicate * ViewWherePredicate
        | Disjunction of ViewWherePredicate * ViewWherePredicate

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
        /// A fully parsed direct-source SELECT projection (no opaque SQL string).
        | CreateView of view: SqlCatalogQualifiedName * source: SqlCatalogQualifiedName *
                        projection: ViewProjection * fromAlias: string option *
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
        | CreateIndex of table: SqlCatalogQualifiedName * name: string * columns: string list
        | DropIndex of name: string * table: SqlCatalogQualifiedName option * ifExists: bool
        /// Exact PostgreSQL index relation identity, never the owning table.
        | DropQualifiedIndex of identity: SqlCatalogQualifiedName * ifExists: bool
        /// One PostgreSQL statement, one atomic multi-index Catalog proof.
        | DropIndexBatch of identities: SqlCatalogQualifiedName list * ifExists: bool
        | AddConstraint of table: SqlCatalogQualifiedName * constraintInfo: SqlCatalogConstraint
        | DropConstraint of table: SqlCatalogQualifiedName * name: string

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

        let columnNames () =
            expectSymbol '('
            let names = ResizeArray<string>()
            names.Add(namePart ())
            while symbol ',' do
                RewriteResourceBudget.checkpoint ()
                names.Add(namePart ())
            expectSymbol ')'
            names |> Seq.toList

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
        let parseCreate () =
            expect "CREATE"
            let viewMode =
                if accept "OR" then
                    // Do not generalize OR REPLACE to unrelated DDL families.
                    if not (accept "REPLACE") then raise OutsideSubset
                    CreateOrReplace
                else CreateOnly
            if accept "VIEW" then
                if sourceDialect <> SqlAgentToolType.Postgres then raise OutsideSubset
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
                        let names = ResizeArray<string>()
                        names.Add(viewHeaderPart ())
                        while symbol ',' do
                            RewriteResourceBudget.checkpoint ()
                            names.Add(viewHeaderPart ())
                        expectSymbol ')'
                        Explicit(names |> Seq.toList)
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
                            let items = ResizeArray<ViewColumnProjection>()
                            items.Add(first)
                            while symbol ',' do
                                RewriteResourceBudget.checkpoint ()
                                match projectionItem () with
                                | Choice1Of2 _ -> raise OutsideSubset
                                | Choice2Of2 next -> items.Add(next)
                            NamedColumns(items |> Seq.toList)
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
                // Never absorb clause words as implicit aliases.
                let fromAlias =
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
                // Proven WHERE subset: atomic direct column comparisons,
                // IS [NOT] NULL, AND/OR with SQL precedence, grouped clauses.
                // The bounded recursive grouping + node count prevents
                // stack-amplification from adversarial View definitions.
                let mutable predicateNodes = 0
                let checkpointPredicate () =
                    RewriteResourceBudget.checkpoint ()
                    predicateNodes <- predicateNodes + 1
                    if predicateNodes > 256 then raise OutsideSubset
                let whereColumn () =
                    let first = namePart ()
                    if symbol '.' then
                        { Qualifier = Some first; SourceColumn = namePart () }
                    else
                        { Qualifier = None; SourceColumn = first }
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
                            let isNotNull = accept "NOT"
                            expect "NULL"
                            NullTest(column, isNotNull)
                        else
                            let op = comparison ()
                            let literal = whereLiteral ()
                            Compare(column, op, literal)
                let predicate =
                    if accept "WHERE" then Some(parseOr 0) else None
                endOfStatement ()
                Recognized(CreateView(view, source, projection, fromAlias,
                                      predicate, viewMode, outputHeader))
            elif viewMode = CreateOrReplace then
                raise OutsideSubset
            elif accept "INDEX" then
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
                    let cols = ResizeArray<string>()
                    cols.Add(physical ())
                    while symbol ',' do
                        RewriteResourceBudget.checkpoint ()
                        cols.Add(physical ())
                    expectSymbol ')'
                    cols |> Seq.toList
                endOfStatement ()
                Recognized(CreateIndex(table, indexName, names))
            else
                if not (accept "TABLE") then raise OutsideSubset
                let conditional = ifNotExists ()
                let table = qualifiedName ()
                expectSymbol '('
                let columns = ResizeArray<SqlCatalogColumn>()
                let constraints = ResizeArray<SqlCatalogConstraint>()
                let optionalConstraintName () =
                    if accept "CONSTRAINT" then Some(namePart ())
                    else None
                let columnList () =
                    expectSymbol '('
                    let cols = ResizeArray<string>()
                    cols.Add(namePart ())
                    while symbol ',' do
                        RewriteResourceBudget.checkpoint ()
                        cols.Add(namePart ())
                    expectSymbol ')'
                    cols |> Seq.toList
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
                    let remoteMembers = columnList ()
                    makeConstraint cname SqlCatalogConstraintKind.ForeignKey
                        members remote remoteMembers
                let tableConstraint () =
                    let cname = optionalConstraintName ()
                    if accept "PRIMARY" then
                        expect "KEY"
                        makeConstraint cname SqlCatalogConstraintKind.PrimaryKey
                            (columnList ()) null []
                    elif accept "UNIQUE" then
                        makeConstraint cname SqlCatalogConstraintKind.Unique
                            (columnList ()) null []
                    elif accept "FOREIGN" then
                        expect "KEY"
                        let members = columnList ()
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
                            let remoteColumns = columnList ()
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
                else Recognized(Create(table, members, deps, conditional))
        let parseDrop () =
            expect "DROP"
            if accept "INDEX" then
                // Keep the legacy single-name and provider ON-table forms.
                // Only PostgreSQL may supply an explicit index schema or
                // the qualified IF EXISTS form in this proven DDL subset.
                let conditional =
                    if sourceDialect = SqlAgentToolType.Postgres && accept "IF" then
                        expect "EXISTS"
                        true
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
                let names = ResizeArray<SqlCatalogQualifiedName>()
                names.Add(view)
                while symbol ',' do
                    RewriteResourceBudget.checkpoint ()
                    names.Add(qualifiedName ())
                let mode =
                    if accept "CASCADE" then Cascade
                    else
                        accept "RESTRICT" |> ignore
                        Restrict
                endOfStatement ()
                if names.Count > 1 then
                    Recognized(DropViewBatch(names |> Seq.toList, conditional, mode))
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
                        let name = namePart ()
                        endOfStatement ()
                        Recognized(DropConstraint(table, name))
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
                    RewriteResourceBudget.checkpoint ()
                    if accept "ADD" then addAction ()
                    elif accept "DROP" then dropAction ()
                    else raise OutsideSubset
                let actions = ResizeArray<AlterColumnAction>()
                actions.Add(parseOne ())
                while symbol ',' do actions.Add(parseOne ())
                actions |> Seq.toList
            if word () = "ADD" && index + 1 < tokens.Length
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
                            let actions = ResizeArray<AlterColumnAction>()
                            actions.Add(addAction ())
                            while symbol ',' do
                                RewriteResourceBudget.checkpoint ()
                                // T-SQL ADD expects a comma-separated column
                                // definition, never another ALTER subcommand.
                                if word () = "DROP" || word () = "ADD" then
                                    raise OutsideSubset
                                actions.Add(Add(column ()))
                            if actions |> Seq.exists (function Add col -> not col.Nullable | _ -> false) then
                                raise OutsideSubset
                            actions |> Seq.toList
                        elif accept "DROP" then
                            expect "COLUMN"
                            let actions = ResizeArray<AlterColumnAction>()
                            actions.Add(Remove(namePart (), false))
                            while symbol ',' do
                                RewriteResourceBudget.checkpoint ()
                                actions.Add(Remove(namePart (), false))
                            actions |> Seq.toList
                        else raise OutsideSubset
                    | SqlAgentToolType.Oracle ->
                        if accept "ADD" then
                            if accept "COLUMN" then raise OutsideSubset
                            let parenthesized = symbol '('
                            let actions = ResizeArray<AlterColumnAction>()
                            actions.Add(Add(column ()))
                            if parenthesized then
                                while symbol ',' do
                                    RewriteResourceBudget.checkpoint ()
                                    actions.Add(Add(column ()))
                                expectSymbol ')'
                            if actions |> Seq.exists (function Add col -> not col.Nullable | _ -> false) then
                                raise OutsideSubset
                            actions |> Seq.toList
                        elif accept "DROP" then
                            if symbol '(' then
                                let actions = ResizeArray<AlterColumnAction>()
                                actions.Add(Remove(namePart (), false))
                                while symbol ',' do
                                    RewriteResourceBudget.checkpoint ()
                                    actions.Add(Remove(namePart (), false))
                                expectSymbol ')'
                                actions |> Seq.toList
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
            | "CREATE" -> parseCreate ()
            | "DROP" -> parseDrop ()
            | "ALTER" -> parseAlter ()
            | _ -> Unsupported
        with
        | Malformed message -> Invalid message
        | OutsideSubset -> Unsupported

    /// Historic caller; preserve source semantics for direct internal uses.
    let parseFor sourceDialect tokens = parseForWith sourceDialect null tokens

    let parse tokens = parseFor SqlAgentToolType.Postgres tokens
