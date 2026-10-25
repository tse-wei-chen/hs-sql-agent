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
        | Deny

    type private ParsedDcl =
        | Modeled of SecurityOperation * SqlTablePrivilege list * SqlCatalogQualifiedName *
                     (string * SqlSecurityPrincipalKind * string option) list
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

    let private mySqlAccountPart maxLength (value: string) allowDotsAndHyphens =
        value.Length > 0 && value.Length <= maxLength
        && (value |> Seq.forall (fun c ->
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9')
            || (allowDotsAndHyphens && (c = '.' || c = '-'))
            || (not allowDotsAndHyphens && c = '_')))

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
            if sourceDialect = SqlAgentToolType.MySQL then
                // The host component is part of the principal identity, not
                // a suffix to discard. Reject MySQL wildcard/matching hosts
                // and all implicit/default-host principal spellings.
                let user =
                    match (take ()).Kind with
                    | StringLiteral text when mySqlAccountPart 32 text false -> text
                    | _ -> raise (UnsupportedGrammar "MySQL accounts require a quoted simple user name.")
                if not (symbol '@') then
                    raise (UnsupportedGrammar "MySQL account must declare an explicit host.")
                let host =
                    match (take ()).Kind with
                    | StringLiteral text when mySqlAccountPart 255 text true -> text
                    | _ -> raise (UnsupportedGrammar "MySQL account host must be an exact quoted ASCII host without wildcards.")
                user, SqlSecurityPrincipalKind.MySqlAccount, Some host
            elif sourceDialect = SqlAgentToolType.Firebird then
                if accept "USER" then (namePart (), SqlSecurityPrincipalKind.User, None)
                elif accept "ROLE" then (namePart (), SqlSecurityPrincipalKind.Role, None)
                elif accept "PUBLIC" then ("PUBLIC", SqlSecurityPrincipalKind.Public, None)
                else raise (UnsupportedGrammar "Firebird grantees require explicit USER, ROLE or PUBLIC to avoid ambiguous identities.")
            elif sourceDialect = SqlAgentToolType.MsSqlServer then
                if word () = "PUBLIC" then
                    raise (UnsupportedGrammar "SQL Server PUBLIC is outside the modeled DCL principal scope.")
                namePart (), SqlSecurityPrincipalKind.Named, None
            elif accept "PUBLIC" then
                "PUBLIC", SqlSecurityPrincipalKind.Public, None
            else
                namePart (), SqlSecurityPrincipalKind.Named, None
        try
            let operation =
                if accept "GRANT" then Grant
                elif accept "REVOKE" then Revoke
                elif accept "DENY" then Deny
                else raise (UnsupportedGrammar "Not a supported DCL statement.")
            // Never map SQL Server DENY onto other vendors' permission models.
            if operation = Deny && sourceDialect <> SqlAgentToolType.MsSqlServer then
                raise (UnsupportedGrammar "DENY is modeled only for SQL Server.")
            match sourceDialect with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.Oracle
            | SqlAgentToolType.Firebird
            | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.MySQL -> ()
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
                    if not (accept "OBJECT") then
                        raise (UnsupportedGrammar "SQL Server DCL requires the proven OBJECT::schema.table form.")
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
                | SqlAgentToolType.MySQL ->
                    // MySQL ON db.table is an exact object spelling; schema.*
                    // and *.* are different grant scopes and stay unsupported.
                    if word () = "TABLE" then
                        raise (UnsupportedGrammar "MySQL table privilege grammar uses ON db.table.")
                    let name = qualifiedName ()
                    if name.Parts.Count <> 2 then
                        raise (UnsupportedGrammar "MySQL modeled table privileges require explicit db.table.")
                    name
                | SqlAgentToolType.Oracle ->
                    // Oracle's object privilege grammar has ON object, not
                    // PostgreSQL's optional TABLE keyword.
                    if word () = "TABLE" then
                        raise (UnsupportedGrammar "Oracle's modeled table privilege grammar uses ON object, without TABLE.")
                    qualifiedName ()
                | SqlAgentToolType.Postgres
                | SqlAgentToolType.Firebird ->
                    accept "TABLE" |> ignore
                    qualifiedName ()
                | _ -> raise (UnsupportedGrammar "Unsupported source dialect.")
            match operation with
            | Grant | Deny -> expect "TO"
            | Revoke -> expect "FROM"
            let principals = ResizeArray<string * SqlSecurityPrincipalKind * string option>()
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
                RewriteFacadeAdapter.tokenizeScriptValidated syntax.Text sourceDialect sourceProfile)
        match parse sourceDialect sourceProfile tokens with
        | Modeled(operation, privileges, target, principals) ->
            let action =
                match operation with
                | Grant -> SqlSecurityAction.Grant
                | Revoke -> SqlSecurityAction.Revoke
                | Deny -> SqlSecurityAction.Deny
            let intents =
                [ for principal, kind, host in principals do
                    for privilege in privileges do
                        RewriteResourceBudget.checkpoint ()
                        yield SqlSecurityIntent(action, privilege, target, principal, kind, syntax.Span,
                                                Option.defaultValue null host) ]
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
