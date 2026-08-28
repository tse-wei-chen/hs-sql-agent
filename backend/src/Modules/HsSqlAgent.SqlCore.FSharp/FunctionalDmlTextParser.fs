namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing

/// INSERT/UPDATE/DELETE statement grammar implemented in F#.
///
/// This owns DML statement shape, mutation-source clauses, RETURNING parsing,
/// and nested query dispatch. Expression parsing is delegated to the F#
/// expression grammar and nested SELECT parsing to the F# query grammar.
module internal FunctionalDmlTextParser =

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    type private DmlGrammar(
        reader: CoreTokenReader,
        sourceDialect: SqlAgentToolType,
        requireExplicitLikeEscape: bool,
        sourceServerVersion: Version | null) as this =

        let expressions =
            lazy (
                FunctionalExpressionTextParser.ExpressionGrammar(
                    reader,
                    Func<SqlStatement>(fun () ->
                        this.ParseNestedQuery()),
                    requireExplicitLikeEscape))

        member private _.Expressions =
            expressions.Value

        member private this.ParseNestedQuery() =
            FunctionalQueryTextParser.parseQueryExpression
                reader
                sourceDialect
                requireExplicitLikeEscape

        member private _.NamedTarget(
            name: SqlIdentifier) =

            NamedTableSource(
                name,
                null,
                name.Span)

        member private _.ValidateReturningSourceContract(
            returningToken: Token) =

            let error =
                SqlDmlReturningCapabilityRules.SourceValidationError(
                    sourceDialect,
                    sourceServerVersion)

            match Option.ofObj error with
            | Some message ->
                raise (CoreTokenReader.Error(
                    message,
                    returningToken))
            | None ->
                ()

        member private _.ParsePostgresMutationSources(
            context: string,
            clauseTerminators: string array) =

            let sources =
                ResizeArray<NamedTableSource>()

            let mutable keepReading = true

            while keepReading do
                let name =
                    reader.ParseIdentifierPath(
                        $"{context} source table")

                sources.Add(
                    NamedTableSource(
                        name,
                        null,
                        name.Span))

                let currentIsTerminator =
                    clauseTerminators
                    |> Array.exists (fun terminator ->
                        reader.PeekWord(terminator))

                if reader.PeekWord("AS")
                   || (reader.Peek().Type = TokenType.Identifier
                       && not currentIsTerminator) then
                    raise (CoreTokenReader.Error(
                        $"{context} aliases are not represented by the current canonical milestone slice.",
                        reader.Peek()))

                keepReading <-
                    reader.Match(TokenType.Comma)

            sources |> toImmutableArray

        member private this.ParseUpdateFromIfPresent() =
            if not (reader.MatchWord("FROM")) then
                ImmutableArray<NamedTableSource>.Empty
            else
                let fromToken = reader.Peek(-1)

                if sourceDialect <> SqlAgentToolType.Postgres then
                    raise (CoreTokenReader.Error(
                        "UPDATE ... FROM source syntax is currently declared only for the PostgreSQL source dialect.",
                        fromToken))

                this.ParsePostgresMutationSources(
                    "UPDATE FROM",
                    [| "WHERE"; "RETURNING" |])

        member private this.ParseDeleteUsingIfPresent() =
            if not (reader.MatchWord("USING")) then
                ImmutableArray<NamedTableSource>.Empty
            else
                let usingToken = reader.Peek(-1)

                if sourceDialect <> SqlAgentToolType.Postgres then
                    raise (CoreTokenReader.Error(
                        "DELETE ... USING source syntax is currently declared only for the PostgreSQL source dialect.",
                        usingToken))

                this.ParsePostgresMutationSources(
                    "DELETE USING",
                    [| "WHERE"; "RETURNING" |])

        member private this.ParsePostgresReturningItem(
            items: ResizeArray<DmlReturningItem>,
            seen: HashSet<string>,
            startToken: Token) =

            let expression =
                this.Expressions.ParseExpression()

            let alias =
                if reader.MatchWord("AS") then
                    Some(
                        CoreTokenReader.ToIdentifierPart(
                            reader.ExpectIdentifier(
                                "RETURNING expression alias")))
                else
                    None

            match expression, alias with
            | (:? ColumnExpr as column), None ->
                if column.Name.Parts.Length <> 1 then
                    raise (CoreTokenReader.Error(
                        "PostgreSQL expression RETURNING currently accepts unqualified target-row columns only; qualified/source-table references remain fail-closed.",
                        startToken))

                let name = column.Name.Parts[0].Value

                if not (seen.Add(name)) then
                    raise (CoreTokenReader.Error(
                        $"RETURNING column '{name}' is declared more than once.",
                        startToken))

                items.Add(
                    DmlReturningColumnItem(
                        column.Name,
                        column.Span))

            | _ ->
                items.Add(
                    DmlReturningExpressionItem(
                        expression,
                        Option.toObj alias,
                        expression.Span))

        member private this.ParseReturningItemsIfPresent() =
            if not (reader.MatchWord("RETURNING")) then
                ImmutableArray<DmlReturningItem>.Empty
            else
                let returningToken =
                    reader.Peek(-1)

                this.ValidateReturningSourceContract(
                    returningToken)

                let items =
                    ResizeArray<DmlReturningItem>()

                let seen =
                    HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)

                let mutable hasWildcard = false
                let mutable keepReading = true

                while keepReading do
                    let token = reader.Peek()

                    if token.Type = TokenType.Operator
                       && token.Value = "*" then

                        reader.Advance() |> ignore

                        if not (seen.Add("*")) then
                            raise (CoreTokenReader.Error(
                                "RETURNING column '*' is declared more than once.",
                                token))

                        hasWildcard <- true

                        items.Add(
                            DmlReturningWildcardItem(
                                CoreTokenReader.Span(token)))

                    elif sourceDialect = SqlAgentToolType.Postgres then
                        this.ParsePostgresReturningItem(
                            items,
                            seen,
                            token)

                    else
                        let column =
                            reader.ParseIdentifierPath(
                                "RETURNING column")

                        if column.Parts.Length <> 1 then
                            raise (CoreTokenReader.Error(
                                "Portable DML RETURNING accepts unqualified target columns only; OLD/NEW/table-qualified and expression result items remain fail-closed.",
                                token))

                        let name = column.Parts[0].Value

                        if not (seen.Add(name)) then
                            raise (CoreTokenReader.Error(
                                $"RETURNING column '{name}' is declared more than once.",
                                token))

                        items.Add(
                            DmlReturningColumnItem(
                                column,
                                column.Span))

                    keepReading <-
                        reader.Match(TokenType.Comma)

                if hasWildcard && items.Count <> 1 then
                    raise (CoreTokenReader.Error(
                        "RETURNING * cannot be mixed with explicit RETURNING columns or expressions in the Core contract.",
                        returningToken))

                items |> toImmutableArray

        member private _.NormalizeUpdateCast(
            expression: SqlExpr,
            token: Token) =

            match expression with
            | :? CastExpr as cast
                when cast.TypeName
                    .Trim()
                    .Equals(
                        "DATE",
                        StringComparison.OrdinalIgnoreCase) ->

                match cast.Expression with
                | :? LiteralExpr as literal ->
                    match literal.Value with
                    | :? string as text ->
                        match SqlTemporalLiteralParser.TryParseDate(text) with
                        | true, date ->
                            LiteralExpr(
                                date,
                                cast.Span)
                            :> SqlExpr
                        | false, _ ->
                            raise (CoreTokenReader.Error(
                                $"Invalid DATE literal '{text}'.",
                                token))
                    | _ ->
                        cast :> SqlExpr
                | _ ->
                    cast :> SqlExpr

            | _ ->
                expression

        member private this.ParseUpdateAssignmentValue() =
            let token = reader.Peek()

            this.NormalizeUpdateCast(
                this.Expressions.ParseExpression(),
                token)

        member private this.ParseInsert() : InsertStatement =
            let start = reader.Position

            reader.ExpectWord("INSERT") |> ignore
            reader.ExpectWord("INTO") |> ignore

            let targetName =
                reader.ParseIdentifierPath(
                    "INSERT target table")

            let target =
                this.NamedTarget(targetName)

            reader.Expect(
                TokenType.LParen,
                "'(' before INSERT column list")
            |> ignore

            let columns =
                ResizeArray<SqlIdentifier>()

            let seen =
                HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)

            let mutable keepColumns = true

            while keepColumns do
                let column =
                    reader.ParseIdentifierPath(
                        "INSERT target column")

                if column.Parts.Length <> 1 then
                    raise (CoreTokenReader.Error(
                        "INSERT target columns must be unqualified.",
                        reader.Peek(-1)))

                let name = column.Parts[0].Value

                if not (seen.Add(name)) then
                    raise (CoreTokenReader.Error(
                        $"INSERT target column '{name}' is declared more than once.",
                        reader.Peek(-1)))

                columns.Add(column)

                keepColumns <-
                    reader.Match(TokenType.Comma)

            reader.Expect(
                TokenType.RParen,
                "')' after INSERT column list")
            |> ignore

            if columns.Count = 0 then
                raise (CoreTokenReader.Error(
                    "INSERT requires at least one target column.",
                    reader.Peek()))

            let source : InsertSource =
                if reader.MatchWord("VALUES") then
                    let rows =
                        ResizeArray<ImmutableArray<SqlExpr>>()

                    let mutable keepRows = true

                    while keepRows do
                        reader.Expect(
                            TokenType.LParen,
                            "'(' before INSERT VALUES row")
                        |> ignore

                        if reader.Peek().Type = TokenType.RParen then
                            raise (CoreTokenReader.Error(
                                "INSERT VALUES row cannot be empty.",
                                reader.Peek()))

                        let values =
                            ResizeArray<SqlExpr>()

                        let mutable keepValues = true

                        while keepValues do
                            values.Add(
                                this.Expressions.ParseExpression())

                            keepValues <-
                                reader.Match(TokenType.Comma)

                        reader.Expect(
                            TokenType.RParen,
                            "')' after INSERT VALUES row")
                        |> ignore

                        if values.Count <> columns.Count then
                            raise (CoreTokenReader.Error(
                                $"INSERT row has {values.Count} values but {columns.Count} columns were declared.",
                                reader.Peek(-1)))

                        rows.Add(
                            values |> toImmutableArray)

                        keepRows <-
                            reader.Match(TokenType.Comma)

                    InsertValuesSource(
                        rows |> toImmutableArray,
                        reader.SpanFrom(start))
                    :> InsertSource

                elif reader.PeekWord("SELECT")
                     || reader.PeekWord("WITH") then

                    InsertQuerySource(
                        this.ParseNestedQuery(),
                        reader.SpanFrom(start))
                    :> InsertSource

                else
                    raise (CoreTokenReader.Error(
                        "INSERT requires VALUES or a SELECT source.",
                        reader.Peek()))

            let returning =
                this.ParseReturningItemsIfPresent()

            CoreParserAstClone.Insert(
                target,
                columns |> toImmutableArray,
                source,
                returning,
                reader.SpanFrom(start))

        member private this.ParseUpdate() : UpdateStatement =
            let start = reader.Position

            reader.ExpectWord("UPDATE") |> ignore

            let targetName =
                reader.ParseIdentifierPath(
                    "UPDATE target table")

            let target =
                this.NamedTarget(targetName)

            if reader.Peek().Type = TokenType.Identifier
               || reader.PeekWord("AS") then
                raise (CoreTokenReader.Error(
                    "UPDATE target aliases are not represented by the Core DML AST.",
                    reader.Peek()))

            reader.ExpectWord("SET") |> ignore

            let assignments =
                ResizeArray<Assignment>()

            let seen =
                HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)

            let mutable keepAssignments = true

            while keepAssignments do
                let assignmentStart = reader.Position

                let column =
                    reader.ParseIdentifierPath(
                        "UPDATE assignment column")

                if column.Parts.Length <> 1 then
                    raise (CoreTokenReader.Error(
                        "UPDATE assignment columns must be unqualified.",
                        reader.Peek(-1)))

                let columnName =
                    column.Parts[0].Value

                if not (seen.Add(columnName)) then
                    raise (CoreTokenReader.Error(
                        $"UPDATE assigns column '{columnName}' more than once.",
                        reader.Peek(-1)))

                let equalsToken = reader.Peek()

                if equalsToken.Type <> TokenType.Operator
                   || equalsToken.Value <> "=" then
                    raise (CoreTokenReader.Error(
                        "Expected '=' in UPDATE assignment.",
                        equalsToken))

                reader.Advance() |> ignore

                assignments.Add(
                    Assignment(
                        column,
                        this.ParseUpdateAssignmentValue(),
                        reader.SpanFrom(assignmentStart)))

                keepAssignments <-
                    reader.Match(TokenType.Comma)

            let fromSources =
                this.ParseUpdateFromIfPresent()

            let predicate =
                if reader.MatchWord("WHERE") then
                    Some(
                        this.Expressions.ParseExpression())
                else
                    None

            let returning =
                this.ParseReturningItemsIfPresent()

            CoreParserAstClone.Update(
                target,
                assignments |> toImmutableArray,
                Option.toObj predicate,
                fromSources,
                returning,
                reader.SpanFrom(start))

        member private this.ParseDelete() : DeleteStatement =
            let start = reader.Position

            reader.ExpectWord("DELETE") |> ignore
            reader.ExpectWord("FROM") |> ignore

            let name =
                reader.ParseIdentifierPath(
                    "DELETE target table")

            let target =
                this.NamedTarget(name)

            if reader.Peek().Type = TokenType.Identifier
               || reader.PeekWord("AS") then
                raise (CoreTokenReader.Error(
                    "DELETE target aliases are not represented by the Core DML AST.",
                    reader.Peek()))

            let usingSources =
                this.ParseDeleteUsingIfPresent()

            let predicate =
                if reader.MatchWord("WHERE") then
                    Some(
                        this.Expressions.ParseExpression())
                else
                    None

            let returning =
                this.ParseReturningItemsIfPresent()

            CoreParserAstClone.Delete(
                target,
                Option.toObj predicate,
                usingSources,
                returning,
                reader.SpanFrom(start))

        member this.ParseComplete() : SqlStatement =
            let statement : SqlStatement =
                if reader.PeekWord("INSERT") then
                    this.ParseInsert() :> SqlStatement
                elif reader.PeekWord("UPDATE") then
                    this.ParseUpdate() :> SqlStatement
                elif reader.PeekWord("DELETE") then
                    this.ParseDelete() :> SqlStatement
                else
                    raise (CoreTokenReader.Error(
                        "Expected INSERT, UPDATE, or DELETE DML statement.",
                        reader.Peek()))

            reader.Match(TokenType.Semicolon)
            |> ignore

            if reader.Peek().Type <> TokenType.EOF then
                raise (CoreTokenReader.Error(
                    $"Unexpected token '{reader.Peek().Value}'; the complete DML statement was not consumed.",
                    reader.Peek()))

            statement

    let parseComplete
        (reader: CoreTokenReader)
        sourceDialect
        requireExplicitLikeEscape
        (sourceServerVersion: Version | null) =

        DmlGrammar(
            reader,
            sourceDialect,
            requireExplicitLikeEscape,
            sourceServerVersion)
            .ParseComplete()
