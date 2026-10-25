namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Small, closed PostgreSQL TABLE DDL grammar. Syntax outside this proven
/// subset is UNSUPPORTED, never treated as successfully analyzed.
module internal RewriteSchemaAnalyzer =

    type DdlOperation =
        | Create of table: string * columns: SqlCatalogColumn list * ifNotExists: bool
        | Drop of table: string * ifExists: bool
        | AddColumn of table: string * column: SqlCatalogColumn
        | RemoveColumn of table: string * column: string * ifExists: bool

    type DdlSyntax =
        | Recognized of DdlOperation
        | Unsupported
        | Invalid of string

    exception private Malformed of string
    exception private OutsideSubset

    let private tokenWord (token: Token) =
        match token.Kind with
        | Keyword value
        | Identifier(value, false) -> value.ToUpperInvariant()
        | _ -> ""

    let parse (tokens: Token[]) =
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
                raise (Malformed("Expected " + value + " in PostgreSQL TABLE DDL."))
        let symbol c =
            match (current ()).Kind with
            | Symbol value when value = c ->
                index <- index + 1
                true
            | _ -> false
        let expectSymbol c =
            if not (symbol c) then
                raise (Malformed("Expected '" + string c + "' in PostgreSQL TABLE DDL."))
        let namePart () =
            match (current ()).Kind with
            | Identifier(name, quoted) ->
                index <- index + 1
                if quoted then name else name.ToLowerInvariant()
            | _ -> raise (Malformed("Expected a SQL identifier in PostgreSQL TABLE DDL."))
        let qualifiedName () =
            let first = namePart ()
            if symbol '.' then first + "." + namePart ()
            else first
        let parseType () =
            let dataType = word ()
            index <- index + 1
            match dataType with
            | "SMALLINT" | "INTEGER" | "INT" | "BIGINT"
            | "TEXT" | "BOOLEAN" | "DATE" | "TIMESTAMP" ->
                dataType
            | "VARCHAR" | "CHAR" ->
                expectSymbol '('
                let length =
                    match (current ()).Kind with
                    | IntegerLiteral number when number > 0L && number <= 1048576L ->
                        index <- index + 1
                        number
                    | _ -> raise (Malformed("Character type length must be a positive integer."))
                expectSymbol ')'
                dataType + "(" + string length + ")"
            | _ -> raise OutsideSubset
        let column () =
            let name = namePart ()
            let typeName = parseType ()
            let nullable =
                if accept "NOT" then
                    expect "NULL"
                    false
                else
                    accept "NULL" |> ignore
                    true
            SqlCatalogColumn(name, typeName, nullable)
        let endOfStatement () =
            match (current ()).Kind with
            | End -> ()
            | _ -> raise OutsideSubset
        let whenTable () =
            expect "TABLE"
            qualifiedName ()
        let tryIfNotExists () =
            if accept "IF" then
                expect "NOT"
                expect "EXISTS"
                true
            else false
        let tryIfExists () =
            if accept "IF" then
                expect "EXISTS"
                true
            else false

        try
            match word () with
            | "CREATE" ->
                expect "CREATE"
                if not (accept "TABLE") then raise OutsideSubset
                let conditional = tryIfNotExists ()
                let tableName = qualifiedName ()
                expectSymbol '('
                let columns = ResizeArray<SqlCatalogColumn>()
                columns.Add(column ())
                while symbol ',' do
                    columns.Add(column ())
                if not (symbol ')') then
                    match (current ()).Kind with
                    | End -> raise (Malformed("CREATE TABLE has no closing ')'."))
                    | _ -> raise OutsideSubset
                endOfStatement ()
                let list = columns |> Seq.toList
                let duplicate =
                    list
                    |> Seq.groupBy (fun item -> item.Name)
                    |> Seq.exists (fun (_, items) -> Seq.length items > 1)
                if duplicate then Invalid "CREATE TABLE contains duplicate column names."
                else Recognized(Create(tableName, list, conditional))
            | "DROP" ->
                expect "DROP"
                if not (accept "TABLE") then raise OutsideSubset
                let conditional = tryIfExists ()
                let tableName = qualifiedName ()
                endOfStatement ()
                Recognized(Drop(tableName, conditional))
            | "ALTER" ->
                expect "ALTER"
                let tableName = whenTable ()
                if accept "ADD" then
                    accept "COLUMN" |> ignore
                    let added = column ()
                    endOfStatement ()
                    Recognized(AddColumn(tableName, added))
                elif accept "DROP" then
                    accept "COLUMN" |> ignore
                    let conditional = tryIfExists ()
                    let removed = namePart ()
                    endOfStatement ()
                    Recognized(RemoveColumn(tableName, removed, conditional))
                else raise OutsideSubset
            | _ -> Unsupported
        with
        | Malformed message -> Invalid message
        | OutsideSubset -> Unsupported
