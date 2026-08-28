namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing

/// Portable INSERT conflict/upsert grammar implemented in F#.
///
/// This owns the pre-DML-parser extraction for PostgreSQL/SQLite ON CONFLICT,
/// MySQL explicit fail-closed behavior, and Firebird UPDATE OR INSERT ...
/// MATCHING canonicalization.
module internal FunctionalDmlConflictParser =

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    let private removeRange
        (tokens: Token array)
        start
        count =

        Array.append
            (tokens |> Array.take start)
            (tokens |> Array.skip (start + count))

    let private isFirebirdUpdateOrInsertStart
        (tokens: Token array) =

        tokens.Length >= 3
        && CoreTokenReader.IsWord(tokens[0], "UPDATE")
        && CoreTokenReader.IsWord(tokens[1], "OR")
        && CoreTokenReader.IsWord(tokens[2], "INSERT")

    let private findRootConflictClause
        (tokens: Token array) =

        let mutable depth = 0
        let mutable result = -1
        let mutable index = 0

        while index + 1 < tokens.Length && result < 0 do
            let token = tokens[index]

            if token.Type = TokenType.LParen then
                depth <- depth + 1
            elif token.Type = TokenType.RParen then
                depth <- Math.Max(0, depth - 1)
            elif depth = 0
                 && CoreTokenReader.IsWord(token, "ON")
                 && (CoreTokenReader.IsWord(tokens[index + 1], "CONFLICT")
                     || CoreTokenReader.IsWord(tokens[index + 1], "DUPLICATE")) then
                result <- index

            index <- index + 1

        result

    let private findRootClauseAfterValues
        (tokens: Token array)
        word =

        let mutable depth = 0
        let mutable sawValues = false
        let mutable result = -1
        let mutable doneSearching = false
        let mutable index = 0

        while index < tokens.Length && not doneSearching do
            let token = tokens[index]

            if token.Type = TokenType.LParen then
                depth <- depth + 1
            elif token.Type = TokenType.RParen then
                depth <- Math.Max(0, depth - 1)
            elif depth = 0 then
                if not sawValues
                   && CoreTokenReader.IsWord(token, "VALUES") then
                    sawValues <- true
                elif sawValues
                     && CoreTokenReader.IsWord(token, word) then
                    result <- index
                    doneSearching <- true
                elif sawValues
                     && (CoreTokenReader.IsWord(token, "RETURNING")
                         || token.Type = TokenType.Semicolon
                         || token.Type = TokenType.EOF) then
                    doneSearching <- true

            index <- index + 1

        result

    let private parseUniqueSinglePartColumns
        (reader: CoreTokenReader)
        description =

        let columns = ResizeArray<SqlIdentifier>()
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable keepReading = true

        while keepReading do
            let token = reader.Peek()
            let column = reader.ParseIdentifierPath(description)

            if column.Parts.Length <> 1 then
                raise (CoreTokenReader.Error(
                    $"{description} must be unqualified.",
                    token))

            let name = column.Parts[0].Value
            if not (seen.Add(name)) then
                raise (CoreTokenReader.Error(
                    $"{description} '{name}' is declared more than once.",
                    token))

            columns.Add(column)
            keepReading <- reader.Match(TokenType.Comma)

        columns |> toImmutableArray

    let private validateTrailer
        (reader: CoreTokenReader) =

        let token = reader.Peek()

        if token.Type = TokenType.EOF
           || token.Type = TokenType.Semicolon
           || reader.PeekWord("RETURNING") then
            ()
        else
            raise (CoreTokenReader.Error(
                "Portable conflict handling supports only the canonical conflict clause followed directly by optional RETURNING; provider-specific predicates, ORDER BY, ROWS, and extra clauses remain fail-closed.",
                token))

    let private validateOnConflictSourceContract
        sourceDialect
        (sourceServerVersion: Version | null)
        token =

        let error =
            SqlDmlUpsertCapabilityRules.OnConflictSourceValidationError(
                sourceDialect,
                sourceServerVersion)

        match Option.ofObj error with
        | Some message ->
            raise (CoreTokenReader.Error(message, token))
        | None ->
            ()

    let private parseAssignments
        (reader: CoreTokenReader) =

        let assignments = ResizeArray<InsertConflictAssignment>()
        let seenTargets = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable keepReading = true

        while keepReading do
            let assignmentStart = reader.Position
            let targetToken = reader.Peek()
            let target =
                reader.ParseIdentifierPath(
                    "ON CONFLICT UPDATE target column")

            if target.Parts.Length <> 1 then
                raise (CoreTokenReader.Error(
                    "ON CONFLICT UPDATE target columns must be unqualified.",
                    targetToken))

            let targetName = target.Parts[0].Value
            if not (seenTargets.Add(targetName)) then
                raise (CoreTokenReader.Error(
                    $"ON CONFLICT UPDATE assigns column '{targetName}' more than once.",
                    targetToken))

            let equalsToken = reader.Peek()
            if equalsToken.Type <> TokenType.Operator
               || equalsToken.Value <> "=" then
                raise (CoreTokenReader.Error(
                    "Expected '=' in ON CONFLICT UPDATE assignment.",
                    equalsToken))

            reader.Advance() |> ignore
            reader.ExpectWord("EXCLUDED") |> ignore
            reader.Expect(
                TokenType.Dot,
                "'.' after EXCLUDED")
            |> ignore

            let sourceToken =
                reader.ExpectIdentifier(
                    "proposed-row column after EXCLUDED.")

            let source =
                SqlIdentifier(
                    ImmutableArray.Create(
                        CoreTokenReader.ToIdentifierPart(sourceToken)),
                    CoreTokenReader.Span(sourceToken))

            assignments.Add(
                InsertConflictAssignment(
                    target,
                    source,
                    reader.SpanFrom(assignmentStart)))

            keepReading <- reader.Match(TokenType.Comma)

        if assignments.Count = 0 then
            raise (CoreTokenReader.Error(
                "ON CONFLICT DO UPDATE requires at least one assignment.",
                reader.Peek()))

        assignments |> toImmutableArray

    let private parseInsertColumns
        (normalizedInsertTokens: Token array) =

        let mutable depth = 0
        let mutable listStart = -1
        let mutable index = 0
        let mutable doneSearching = false

        while index < normalizedInsertTokens.Length
              && not doneSearching do

            let token = normalizedInsertTokens[index]

            if depth = 0 && token.Type = TokenType.LParen then
                listStart <- index
                doneSearching <- true
            elif CoreTokenReader.IsWord(token, "VALUES") then
                doneSearching <- true
            else
                if token.Type = TokenType.LParen then
                    depth <- depth + 1
                elif token.Type = TokenType.RParen then
                    depth <- Math.Max(0, depth - 1)

            index <- index + 1

        if listStart < 0 then
            raise (CoreTokenReader.Error(
                "Portable Firebird UPDATE OR INSERT requires an explicit INSERT column list.",
                normalizedInsertTokens[0]))

        let reader =
            CoreTokenReader(
                normalizedInsertTokens[(listStart + 1)..])

        let columns =
            parseUniqueSinglePartColumns
                reader
                "Firebird UPDATE OR INSERT column"

        reader.Expect(
            TokenType.RParen,
            "')' after Firebird UPDATE OR INSERT column list")
        |> ignore

        if columns.IsDefaultOrEmpty then
            raise (CoreTokenReader.Error(
                "Firebird UPDATE OR INSERT requires at least one explicit column.",
                reader.Peek()))

        columns

    let private extractFirebirdUpdateOrInsert
        (tokens: Token array)
        sourceDialect =

        let sourceError =
            SqlDmlUpsertCapabilityRules.FirebirdUpdateOrInsertSourceValidationError(
                sourceDialect)

        match Option.ofObj sourceError with
        | Some message ->
            raise (CoreTokenReader.Error(message, tokens[0]))
        | None ->
            ()

        let normalizedPrefix =
            tokens |> Array.skip 2

        let matchingIndex =
            findRootClauseAfterValues
                normalizedPrefix
                "MATCHING"

        if matchingIndex < 0 then
            raise (CoreTokenReader.Error(
                "Portable Firebird UPDATE OR INSERT requires an explicit MATCHING column list; implicit primary-key matching is not canonicalized without source metadata.",
                tokens[0]))

        let reader =
            CoreTokenReader(
                normalizedPrefix[matchingIndex..])

        let start = reader.Position
        let matchingToken =
            reader.ExpectWord("MATCHING")

        reader.Expect(
            TokenType.LParen,
            "'(' before Firebird MATCHING column list")
        |> ignore

        let targetColumns =
            parseUniqueSinglePartColumns
                reader
                "Firebird MATCHING column"

        reader.Expect(
            TokenType.RParen,
            "')' after Firebird MATCHING column list")
        |> ignore

        if targetColumns.IsDefaultOrEmpty then
            raise (CoreTokenReader.Error(
                "Firebird MATCHING requires at least one explicit column.",
                matchingToken))

        validateTrailer reader

        let insertColumns =
            parseInsertColumns normalizedPrefix

        let assignments =
            insertColumns
            |> Seq.map (fun column ->
                InsertConflictAssignment(
                    column,
                    column,
                    column.Span))
            |> toImmutableArray

        let conflict =
            InsertConflictClause(
                targetColumns,
                InsertConflictActionKind.UpdateProposedValues,
                assignments,
                reader.SpanFrom(start))

        struct (
            removeRange
                normalizedPrefix
                matchingIndex
                reader.Position,
            conflict)

    /// Extract the portable conflict clause and return a token stream consumable
    /// by the ordinary DML grammar parser.
    let extract
        (tokens: Token array)
        sourceDialect
        (sourceServerVersion: Version | null)
        : struct (Token array * InsertConflictClause option) =

        if isNull tokens then
            raise (ArgumentNullException("tokens"))

        if isFirebirdUpdateOrInsertStart tokens then
            let struct (normalized, conflict) =
                extractFirebirdUpdateOrInsert
                    tokens
                    sourceDialect

            struct (normalized, Some conflict)

        elif tokens.Length = 0
             || not (CoreTokenReader.IsWord(tokens[0], "INSERT")) then
            struct (tokens, None)

        else
            let onIndex = findRootConflictClause tokens

            if onIndex < 0 then
                struct (tokens, None)
            else
                let reader =
                    CoreTokenReader(tokens[onIndex..])

                let start = reader.Position
                let onToken = reader.ExpectWord("ON")

                if not (reader.MatchWord("CONFLICT")) then
                    let sourceGrammar =
                        SqlSourceDialectGrammarRules.For(
                            sourceDialect)

                    if sourceGrammar.SupportsOnDuplicateKeyUpsertSyntax
                       && reader.PeekWord("DUPLICATE") then
                        raise (CoreTokenReader.Error(
                            "MySQL ON DUPLICATE KEY UPDATE has no explicit conflict target, so Core cannot translate it to the deterministic portable ON CONFLICT contract.",
                            onToken))

                    raise (CoreTokenReader.Error(
                        "Portable INSERT conflict handling requires an explicit ON CONFLICT clause.",
                        onToken))

                validateOnConflictSourceContract
                    sourceDialect
                    sourceServerVersion
                    onToken

                reader.Expect(
                    TokenType.LParen,
                    "'(' before ON CONFLICT target column list")
                |> ignore

                let targetColumns =
                    parseUniqueSinglePartColumns
                        reader
                        "ON CONFLICT target column"

                reader.Expect(
                    TokenType.RParen,
                    "')' after ON CONFLICT target column list")
                |> ignore

                if targetColumns.IsDefaultOrEmpty then
                    raise (CoreTokenReader.Error(
                        "ON CONFLICT requires at least one explicit target column.",
                        onToken))

                reader.ExpectWord("DO") |> ignore

                let action, assignments =
                    if reader.MatchWord("NOTHING") then
                        InsertConflictActionKind.DoNothing,
                        ImmutableArray<InsertConflictAssignment>.Empty
                    else
                        reader.ExpectWord("UPDATE") |> ignore
                        reader.ExpectWord("SET") |> ignore
                        InsertConflictActionKind.UpdateProposedValues,
                        parseAssignments reader

                validateTrailer reader

                let conflict =
                    InsertConflictClause(
                        targetColumns,
                        action,
                        assignments,
                        reader.SpanFrom(start))

                struct (
                    removeRange
                        tokens
                        onIndex
                        reader.Position,
                    Some conflict)
