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

    type DdlOperation =
        | Create of table: string * columns: SqlCatalogColumn list * ifNotExists: bool
        | Drop of table: string * ifExists: bool
        | AlterColumns of table: string * actions: AlterColumnAction list

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
            match sourceDialect with
            | SqlAgentToolType.Postgres -> if quoted then name else name.ToLowerInvariant()
            | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird ->
                if quoted then name else name.ToUpperInvariant()
            | SqlAgentToolType.MySQL ->
                // MySQL identifier casing depends on lower_case_table_names
                // and the server filesystem. Retain source spelling until a
                // provider-profile collation/casing proof is available.
                name
            | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
                // Matches the conservative existing snapshot name binder.
                name.ToUpperInvariant()
            | _ -> name
        let namePart () =
            match (current ()).Kind with
            | Identifier(name, quoted) ->
                index <- index + 1
                normalizeName name quoted
            | _ -> raise (Malformed("Expected a SQL identifier in TABLE DDL."))
        let qualifiedName () =
            let first = namePart ()
            if symbol '.' then first + "." + namePart ()
            else first

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
            if not (accept "TABLE") then raise OutsideSubset
            let conditional = ifNotExists ()
            let table = qualifiedName ()
            expectSymbol '('
            let columns = ResizeArray<SqlCatalogColumn>()
            columns.Add(column ())
            while symbol ',' do columns.Add(column ())
            if not (symbol ')') then
                match (current ()).Kind with
                | End -> raise (Malformed("CREATE TABLE has no closing ')'."))
                | _ -> raise OutsideSubset
            endOfStatement ()
            let members = columns |> Seq.toList
            let duplicate =
                members |> Seq.groupBy (fun x -> x.Name)
                |> Seq.exists (fun (_, group) -> Seq.length group > 1)
            if duplicate then Invalid "CREATE TABLE contains duplicate column names."
            else Recognized(Create(table, members, conditional))
        let parseDrop () =
            expect "DROP"
            if not (accept "TABLE") then raise OutsideSubset
            let conditional = ifExists ()
            let table = qualifiedName ()
            endOfStatement ()
            Recognized(Drop(table, conditional))
        let parseAlter () =
            expect "ALTER"
            if not (accept "TABLE") then raise OutsideSubset
            let table = qualifiedName ()
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
