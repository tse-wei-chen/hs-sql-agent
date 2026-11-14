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
        | ModeledTable of SecurityOperation * SqlTablePrivilege list * SqlCatalogQualifiedName *
                          (string * SqlSecurityPrincipalKind * string option) list *
                          withGrantOption: bool * revokeGrantOptionOnly: bool *
                          cascadeMode: SqlSecurityCascadeMode
        | ModeledRole of SecurityOperation * string list *
                         (string * SqlSecurityPrincipalKind * string option) list *
                         withAdminOption: bool * revokeAdminOptionOnly: bool *
                         cascadeMode: SqlSecurityCascadeMode
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
            // Exact-source PostgreSQL role membership is a separate grammar
            // from table privilege delegation. Never reuse it for other DBs.
            let revokeGrantOptionOnly, revokeAdminOptionOnly =
                if operation = Revoke && accept "GRANT" then
                    expect "OPTION"
                    expect "FOR"
                    true, false
                elif operation = Revoke && accept "ADMIN" then
                    expect "OPTION"
                    expect "FOR"
                    false, true
                else false, false
            if (revokeGrantOptionOnly || revokeAdminOptionOnly)
                && sourceDialect <> SqlAgentToolType.Postgres then
                raise (UnsupportedGrammar "Grant/admin-option revocation requires the PostgreSQL proof subset.")

            let readPrincipals () =
                let principals = ResizeArray<string * SqlSecurityPrincipalKind * string option>()
                principals.Add(grantee ())
                while symbol ',' do
                    RewriteResourceBudget.checkpoint ()
                    if principals.Count >= MaxIntentsPerStatement then
                        raise (UnsupportedGrammar "DCL recipient list exceeds the static budget.")
                    principals.Add(grantee ())
                List.ofSeq principals

            let readCascade () =
                if operation <> Revoke then SqlSecurityCascadeMode.None
                elif accept "CASCADE" then SqlSecurityCascadeMode.Cascade
                elif accept "RESTRICT" then SqlSecurityCascadeMode.Restrict
                else SqlSecurityCascadeMode.None

            let isTablePrivilege =
                match word () with
                | "SELECT" | "INSERT" | "UPDATE" | "DELETE" -> true
                | _ -> false
            if revokeAdminOptionOnly || not isTablePrivilege then
                if sourceDialect <> SqlAgentToolType.Postgres
                   || operation = Deny || revokeGrantOptionOnly then
                    raise (UnsupportedGrammar "Role membership syntax is modeled for PostgreSQL GRANT/REVOKE only.")
                if word () = "ALL" || word () = "PUBLIC" then
                    raise (UnsupportedGrammar "Wildcard and PUBLIC role membership are unproven.")
                let roles = ResizeArray<string>()
                roles.Add(namePart ())
                while symbol ',' do
                    RewriteResourceBudget.checkpoint ()
                    if roles.Count >= MaxIntentsPerStatement then
                        raise (UnsupportedGrammar "Role membership expansion exceeds budget.")
                    roles.Add(namePart ())
                match operation with
                | Grant -> expect "TO"
                | Revoke -> expect "FROM"
                | Deny -> raise (UnsupportedGrammar "DENY does not grant role membership.")
                let principals = readPrincipals ()
                if principals |> List.exists (fun (name,kind,_) ->
                    kind = SqlSecurityPrincipalKind.Public
                    || String.Equals(name, "PUBLIC", StringComparison.OrdinalIgnoreCase)) then
                    raise (UnsupportedGrammar "PostgreSQL role membership cannot target PUBLIC.")
                let withAdmin =
                    if operation = Grant && accept "WITH" then
                        expect "ADMIN"
                        expect "OPTION"
                        true
                    else false
                let cascade = readCascade ()
                if index <> values.Length then
                    raise (UnsupportedGrammar "Unmodeled role options, grantor or trailing clauses.")
                if roles.Count * principals.Length > MaxIntentsPerStatement then
                    raise (UnsupportedGrammar "Role dependency expansion exceeds the static budget.")
                ModeledRole(operation, List.ofSeq roles, principals,
                            withAdmin, revokeAdminOptionOnly, cascade)
            else
                if revokeAdminOptionOnly then
                    raise (UnsupportedGrammar "ADMIN OPTION FOR applies to role membership only.")
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
                        match current () with
                        | Some token when token.Kind = Operator "::" -> index <- index + 1
                        | _ when symbol ':' && symbol ':' -> ()
                        | _ -> raise (UnsupportedGrammar "SQL Server DCL requires OBJECT::schema.table.")
                        let name = qualifiedName ()
                        if name.Parts.Count <> 2 then
                            raise (UnsupportedGrammar "SQL Server DCL requires an explicit schema-qualified object.")
                        name
                    | SqlAgentToolType.MySQL ->
                        if word () = "TABLE" then
                            raise (UnsupportedGrammar "MySQL table privilege grammar uses ON db.table.")
                        let name = qualifiedName ()
                        if name.Parts.Count <> 2 then
                            raise (UnsupportedGrammar "MySQL modeled table privileges require explicit db.table.")
                        name
                    | SqlAgentToolType.Oracle ->
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
                let principals = readPrincipals ()
                let withGrantOption =
                    if operation = Grant && sourceDialect = SqlAgentToolType.Postgres
                       && accept "WITH" then
                        expect "GRANT"
                        expect "OPTION"
                        true
                    else false
                if withGrantOption && (principals |> List.exists (fun (_,k,_) ->
                    k = SqlSecurityPrincipalKind.Public)) then
                    raise (UnsupportedGrammar "PostgreSQL cannot grant GRANT OPTION to PUBLIC.")
                let cascade = readCascade ()
                if index <> values.Length then
                    raise (UnsupportedGrammar "Unmodeled privilege options, column grants, grantor or trailing clauses.")
                if principals.Length * privileges.Count > MaxIntentsPerStatement then
                    raise (UnsupportedGrammar "DCL intent expansion exceeds static resource budget.")
                ModeledTable(operation, List.ofSeq privileges, target, principals,
                             withGrantOption, revokeGrantOptionOnly, cascade)
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
        let action = function
            | Grant -> SqlSecurityAction.Grant
            | Revoke -> SqlSecurityAction.Revoke
            | Deny -> SqlSecurityAction.Deny
        match parse sourceDialect sourceProfile tokens with
        | ModeledTable(operation, privileges, target, principals,
                       withGrantOption, revokeOnly, cascadeMode) ->
            let sqlAction = action operation
            let intents =
                [ for principal, kind, host in principals do
                    for privilege in privileges do
                        RewriteResourceBudget.checkpoint ()
                        yield SqlSecurityIntent(sqlAction, privilege, target, principal, kind, syntax.Span,
                                                Option.defaultValue null host, withGrantOption,
                                                revokeOnly, cascadeMode) ]
            // A grant without grant-option has no modeled outgoing delegation
            // edge. A revoke could invalidate one, even with no prior DCL
            // in this script. We cannot identify who actually depends on it.
            let dependencies =
                if sourceDialect = SqlAgentToolType.Postgres
                   && (withGrantOption || operation = Revoke) then
                    [ for intent in intents do
                        RewriteResourceBudget.checkpoint ()
                        yield SqlSecurityDependency(
                            SqlSecurityDependencyKind.TableDelegation,
                            sqlAction, intent.Principal, intent.PrincipalKind,
                            null, target, Nullable(intent.Privilege),
                            withGrantOption, revokeOnly, cascadeMode, syntax.Span) ]
                else []
            SqlStatementAnalysisStatus.PartiallyAnalyzed, intents, dependencies, []
        | ModeledRole(operation, roles, principals, withAdmin, revokeOnly, cascadeMode) ->
            let sqlAction = action operation
            let dependencies =
                [ for principal, kind, _ in principals do
                    for role in roles do
                        RewriteResourceBudget.checkpoint ()
                        yield SqlSecurityDependency(
                            SqlSecurityDependencyKind.RoleMembership,
                            sqlAction, principal, kind, role, null,
                            Nullable<SqlTablePrivilege>(),
                            withAdmin, revokeOnly, cascadeMode, syntax.Span) ]
            SqlStatementAnalysisStatus.PartiallyAnalyzed, [], dependencies, []
        | Unsupported message ->
            SqlStatementAnalysisStatus.Unsupported, [], [],
            [diagnostic syntax "SQL_SECURITY_UNSUPPORTED"
                SqlDiagnosticCategory.Capability message]
        | Invalid message ->
            SqlStatementAnalysisStatus.Invalid, [], [],
            [diagnostic syntax "SQL_SECURITY_SYNTAX_INVALID"
                SqlDiagnosticCategory.Syntax message]
