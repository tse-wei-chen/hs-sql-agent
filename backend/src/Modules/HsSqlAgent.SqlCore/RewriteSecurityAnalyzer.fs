namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// A deliberately bounded, closed DCL grammar. No ACL is read, changed, or
/// implied, and these syntactic intents are never authorization evidence.
/// GRANT/REVOKE may fail, be ignored, require grant-option or involve inherited
/// roles; no transition to an effective privilege state is modeled.
module internal RewriteSecurityAnalyzer =

    [<Literal>]
    let MaxIntentsPerStatement = 128

    type private SecurityOperation =
        | Grant
        | Revoke

    type private ParsedDcl =
        | Modeled of SecurityOperation * SqlTablePrivilege list * SqlCatalogQualifiedName *
                     (string * SqlSecurityPrincipalKind) list
        | Unsupported of string
        | Invalid of string

    exception private UnsupportedGrammar of string
    exception private InvalidGrammar of string

    let private tokenWord (token: Token) =
        match token.Kind with
        | Keyword value
        | Identifier(value, false) -> value.ToUpperInvariant()
        | _ -> ""

    let private isSymbol value (token: Token) =
        match token.Kind with
        | Symbol c -> c = value
        | _ -> false

    let private readOnlyName parts =
        SqlCatalogQualifiedName(parts |> List.toArray |> Array.AsReadOnly)

    let private validName (name: string) quoted =
        // Lexical tokens already separate quoted names from delimiters and
        // string literals. Do not claim server collation or object existence.
        not (String.IsNullOrWhiteSpace name)
        && (quoted || RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier name)

    let private parse sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                      (tokens: Token list) : ParsedDcl =
        let values = tokens |> List.filter (fun t -> t.Kind <> End) |> List.toArray
        let mutable index = 0
        let current () =
            if index < values.Length then Some values[index] else None
        let word () =
            match current () with
            | Some t -> tokenWord t
            | None -> ""
        let take () =
            match current () with
            | Some value ->
                index <- index + 1
                value
            | None -> raise (InvalidGrammar "Unexpected end of DCL statement.")
        let accept value =
            if word () = value then
                index <- index + 1
                true
            else false
        let expect value =
            if not (accept value) then
                raise (InvalidGrammar ("Expected " + value + " in DCL table privilege statement."))
        let symbol ch =
            match current () with
            | Some t when isSymbol ch t ->
                index <- index + 1
                true
            | _ -> false
        let namePart () =
            match (take ()).Kind with
            | Identifier(name, quoted) when validName name quoted ->
                RewriteCatalogIdentifierProof.canonicalName sourceDialect sourceProfile name quoted
            | _ -> raise (InvalidGrammar "Expected a plain or quoted SQL identifier.")
        let qualifiedName () =
            let first = namePart ()
            let parts =
                if symbol '.' then
                    [first; namePart ()]
                else [first]
            readOnlyName parts
        let privilege () =
            match word () with
            | "SELECT" -> index <- index + 1; SqlTablePrivilege.Select
            | "INSERT" -> index <- index + 1; SqlTablePrivilege.Insert
            | "UPDATE" -> index <- index + 1; SqlTablePrivilege.Update
            | "DELETE" -> index <- index + 1; SqlTablePrivilege.Delete
            | _ -> raise (UnsupportedGrammar "Only whole-table SELECT, INSERT, UPDATE and DELETE privileges are modeled.")
        let grantee () =
            if sourceDialect = SqlAgentToolType.Firebird then
                if accept "USER" then (namePart (), SqlSecurityPrincipalKind.User)
                elif accept "ROLE" then (namePart (), SqlSecurityPrincipalKind.Role)
                elif accept "PUBLIC" then ("PUBLIC", SqlSecurityPrincipalKind.Public)
                else raise (UnsupportedGrammar "Firebird grantees require explicit USER, ROLE or PUBLIC to avoid ambiguous identities.")
            elif sourceDialect <> SqlAgentToolType.MsSqlServer && accept "PUBLIC" then
                "PUBLIC", SqlSecurityPrincipalKind.Public
            else
                namePart (), SqlSecurityPrincipalKind.Named
        try
            let operation =
                if accept "GRANT" then Grant
                elif accept "REVOKE" then Revoke
                else raise (UnsupportedGrammar "Not a supported GRANT or REVOKE statement.")
            // A provider grammar is a proof gate, not a generic ANSI
            // permission fallback. MySQL users include host/account semantics;
            // SQLite does not provide SQL GRANT/REVOKE.
            match sourceDialect with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.Oracle
            | SqlAgentToolType.Firebird
            | SqlAgentToolType.MsSqlServer -> ()
            | _ -> raise (UnsupportedGrammar "This provider's DCL account and object privilege grammar is not modeled.")
            let privileges = ResizeArray<SqlTablePrivilege>()
            privileges.Add(privilege ())
            while symbol ',' do
                RewriteResourceBudget.checkpoint ()
                if privileges.Count >= 4 then
                    raise (UnsupportedGrammar "DCL duplicate or unsupported privilege list.")
                let next = privilege ()
                if privileges.Contains(next) then
                    raise (UnsupportedGrammar "Repeated DCL privilege is outside the canonical static intent subset.")
                privileges.Add next
            expect "ON"
            let target =
                match sourceDialect with
                | SqlAgentToolType.MsSqlServer ->
                    expect "OBJECT"
                    // SQL Server object qualifier must use OBJECT::, not an
                    // invented portable TABLE notation.
                    match current () with
                    | Some token when token.Kind = Operator "::" ->
                        index <- index + 1
                    | _ when symbol ':' && symbol ':' -> ()
                    | _ -> raise (UnsupportedGrammar "SQL Server DCL requires OBJECT::schema.table.")
                    let name = qualifiedName ()
                    if name.Parts.Count <> 2 then
                        raise (UnsupportedGrammar "SQL Server DCL requires an explicit schema-qualified object.")
                    name
                | SqlAgentToolType.Oracle ->
                    // Oracle's object privilege grammar has ON object, not
                    // PostgreSQL's optional TABLE keyword.
                    qualifiedName ()
                | SqlAgentToolType.Postgres
                | SqlAgentToolType.Firebird ->
                    accept "TABLE" |> ignore
                    qualifiedName ()
                | _ -> raise (UnsupportedGrammar "Unsupported source dialect.")
            match operation with
            | Grant -> expect "TO"
            | Revoke -> expect "FROM"
            let principals = ResizeArray<string * SqlSecurityPrincipalKind>()
            principals.Add(grantee ())
            while symbol ',' do
                RewriteResourceBudget.checkpoint ()
                if principals.Count >= MaxIntentsPerStatement then
                    raise (UnsupportedGrammar "DCL expansion exceeds static intent budget.")
                principals.Add(grantee ())
            if index <> values.Length then
                raise (UnsupportedGrammar "DCL trailing clauses, column privileges, membership, grants with options and cascading effects are not modeled.")
            if principals.Count * privileges.Count > MaxIntentsPerStatement then
                raise (UnsupportedGrammar "DCL intent expansion exceeds static resource budget.")
            Modeled(operation, List.ofSeq privileges, target, List.ofSeq principals)
        with
        | UnsupportedGrammar message -> Unsupported message
        | InvalidGrammar message -> Invalid message

    let private diagnostic (syntax: SqlScriptStatementSyntax) code category message =
        SqlDiagnostic(code, SqlDiagnosticStage.SemanticValidation, category, message, syntax.Span)

    let analyze sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "SecurityLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
        match parse sourceDialect sourceProfile tokens with
        | Modeled(operation, privileges, target, principals) ->
            let action =
                match operation with
                | Grant -> SqlSecurityAction.Grant
                | Revoke -> SqlSecurityAction.Revoke
            let intents =
                [ for principal, kind in principals do
                    for privilege in privileges do
                        RewriteResourceBudget.checkpoint ()
                        yield SqlSecurityIntent(action, privilege, target, principal, kind, syntax.Span) ]
            // Only the grammar/intent is modeled. Privilege application,
            // authorization, object existence and grantor are UNKNOWN.
            SqlStatementAnalysisStatus.PartiallyAnalyzed, intents, []
        | Unsupported message ->
            SqlStatementAnalysisStatus.Unsupported, [],
            [diagnostic syntax "SQL_SECURITY_UNSUPPORTED"
                SqlDiagnosticCategory.Capability message]
        | Invalid message ->
            SqlStatementAnalysisStatus.Invalid, [],
            [diagnostic syntax "SQL_SECURITY_SYNTAX_INVALID"
                SqlDiagnosticCategory.Syntax message]
