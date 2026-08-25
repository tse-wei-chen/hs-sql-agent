using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

public enum SqlCapabilityStatus
{
    Supported,
    Translated,
    Rejected
}

public sealed record SqlCapability(
    string Id,
    string Category,
    SqlCapabilityStatus Status,
    string Detail);

public sealed record ProviderSqlCapabilities(
    string MatrixVersion,
    SqlAgentToolType Provider,
    IReadOnlyList<SqlCapability> Capabilities);

public static class SqlCapabilityMatrix
{
    public const string Version = "2026-08-25.30";

    public static ProviderSqlCapabilities ForProvider(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        if (targetProfile is not null && targetProfile.Provider != provider)
        {
            throw new ArgumentException(
                $"Target capability profile declares provider {targetProfile.Provider}, but matrix provider is {provider}.",
                nameof(targetProfile));
        }

        var sqlServerRegexEnabled = provider == SqlAgentToolType.MsSqlServer
            && targetProfile is { CompatibilityLevel: >= 170 };

        var capabilities = new List<SqlCapability>
        {
            new("provider.target_profile", "provider", SqlCapabilityStatus.Supported,
                "Core accepts optional target runtime metadata including server version, compatibility level, session modes, and session settings. Undeclared target-profile-dependent capabilities remain fail-closed; SQL Server REGEXP_LIKE is enabled only by a declared target profile at compatibility level 170+.") ,
            new("provider.source_profile", "provider", SqlCapabilityStatus.Supported,
                "Raw SQL compilation accepts a separate optional source runtime profile for session-dependent source semantics. The source profile provider must match the parsed source dialect and never authorizes target capabilities. MySQL source || is resolved as concatenation only when PIPES_AS_CONCAT or ANSI is explicitly declared; MySQL double-quoted identifiers are accepted only when ANSI_QUOTES or ANSI is explicitly declared. MySQL backslash-containing single-quoted strings and quoted identifiers use ordinary-character semantics only when NO_BACKSLASH_ESCAPES is explicitly declared; ANSI does not imply NO_BACKSLASH_ESCAPES. Under NO_BACKSLASH_ESCAPES, raw MySQL LIKE is accepted only when the source declares an explicit single-character ESCAPE clause; omitting that contract remains fail-closed rather than guessing pattern escape semantics. Absent or unrelated modes remain fail-closed rather than guessing session sql_mode."),
            new("select.basic", "query", SqlCapabilityStatus.Translated,
                "SELECT/JOIN/WHERE/GROUP BY/HAVING/ORDER BY within the structured Core grammar."),
            new("select.row_limit", "query", SqlCapabilityStatus.Translated,
                "Structured Core row-count limits are translated to provider-native target syntax. Raw LIMIT spelling is accepted only for PostgreSQL, MySQL, and SQLite source dialects. PostgreSQL LIMIT ALL is canonicalized to no row-count limit, including LIMIT ALL OFFSET n where only the offset remains; MySQL and SQLite reject LIMIT ALL. MySQL and SQLite additionally accept native LIMIT offset,row_count and canonicalize the first integer to OFFSET and the second to LIMIT; PostgreSQL comma-form LIMIT is rejected. Raw bare OFFSET remains valid PostgreSQL syntax; MySQL and SQLite accept OFFSET only after LIMIT, and comma-form LIMIT cannot be combined with a separate OFFSET clause. PostgreSQL, Oracle, and Firebird raw source may use the modeled SQL-standard integer OFFSET ... ROW(S) and FETCH FIRST/NEXT ... ROW(S) ONLY forms, including FETCH without OFFSET; PostgreSQL may omit ROW/ROWS after OFFSET and may omit the FETCH count, which canonicalizes to one row. Explicit LIMIT and FETCH clauses remain mutually exclusive at the raw source boundary, including LIMIT ALL, matching PostgreSQL's alternative-syntax grammar. SQL Server raw OFFSET/FETCH requires statement-level ORDER BY, FETCH requires a preceding OFFSET, and TOP cannot share the same query scope. FETCH PERCENT, WITH TIES, and non-integer row-count expressions remain fail-closed because those semantics are not represented by the canonical Limit/Offset model."),
            new("select.singleton", "query", SqlCapabilityStatus.Translated,
                "SELECT expressions without a FROM source preserve singleton-row semantics; Oracle lowers through DUAL and Firebird through RDB$DATABASE. Free column references and wildcard projection fail closed instead of resolving against a provider dummy table, while COUNT(*) and correlated outer references remain valid."),
            new("select.cte_set", "query", SqlCapabilityStatus.Translated,
                "Statement-root CTEs and UNION/INTERSECT/EXCEPT are represented structurally. Root CTE set queries that need an outer ORDER BY/LIMIT/OFFSET wrapper move only the root CTE definitions to that generated wrapper so SqlKata cannot drop their scope; this also covers execution-policy limits."),
            new("select.cte_derived", "query",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? "Derived-table-local CTEs are compiled as complete target subqueries and reattached with ordered bindings, preserving lexical scope without CTE hoisting. The Core provider compiler applies the same rewrite before every nested SELECT, so query, scalar/EXISTS, and DML subqueries share this behavior; derived CTE set queries with an outer tail are included."
                    : provider == SqlAgentToolType.Oracle
                        ? "Oracle rejects WITH clauses nested inside parenthesized subqueries, so derived-table-local CTEs fail closed instead of emitting ORA-32034-prone SQL."
                        : provider == SqlAgentToolType.MsSqlServer
                            ? "SQL Server has no declared portable WITH-at-the-start-of-a-general-derived-subquery contract in the Core target profile, so derived-table-local CTEs fail closed in query and DML contexts."
                            : "Firebird nested CTE placement is kept fail-closed in query and DML contexts until a target-profile contract is modeled and integration-tested."),
            new("select.cte_set_branch", "query",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? "Set-operation branches with a statement-root CTE are fully compiled as target fragments and wrapped behind a CTE-free derived SELECT before UNION/INTERSECT/EXCEPT lowering. The provider compiler applies this to ordinary, scalar/EXISTS, and DML nested SELECT compilation while preserving branch scope, tail clauses, and ordered bindings."
                    : provider == SqlAgentToolType.Oracle
                        ? "Oracle rejects the nested parenthesized WITH form required by the current set-branch wrapper, so set-branch-local CTEs fail closed."
                        : provider == SqlAgentToolType.MsSqlServer
                            ? "SQL Server has no declared portable nested-WITH branch wrapper contract in the Core target profile, so set-branch-local CTEs fail closed in query and DML contexts."
                            : "Firebird nested CTE placement is kept fail-closed for set branches until a target-profile contract is modeled and integration-tested."),
            new("select.cte_scalar_root", "query",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? "Scalar and EXISTS subqueries may own a statement-root WITH clause. Core renders those expressions through a complete provider compiler invocation, preserving the root CTE, correlated outer references, and ordered bindings. Root CTE set queries that require an outer ORDER BY/LIMIT/OFFSET wrapper remain tracked by select.cte_scope."
                    : provider == SqlAgentToolType.Oracle
                        ? "Oracle rejects WITH inside the parenthesized scalar/EXISTS subquery form, so scalar-root CTEs fail closed."
                        : provider == SqlAgentToolType.MsSqlServer
                            ? "SQL Server does not permit a nested WITH clause in the Core general-subquery profile, so scalar/EXISTS root CTEs fail closed."
                            : "Firebird scalar/EXISTS root CTE placement remains fail-closed until a target-profile contract is modeled and integration-tested."),
            new("select.cte_definition_local", "query",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? "A CTE body may declare its own local WITH scope. Core recursively prepares deeper scopes, fully compiles the CTE body, and reattaches it as a raw CTE component so SqlKata CteFinder cannot hoist local definitions. Same-name shadowing, positional binding order, and local set-operation bodies with outer ORDER BY/LIMIT/OFFSET are preserved; CTE definitions have no parent correlation scope in the Core binder."
                    : provider == SqlAgentToolType.Oracle
                        ? "Oracle does not support nesting a WITH clause inside another WITH query block in the Core target profile, so CTE-definition-local WITH fails closed."
                        : provider == SqlAgentToolType.MsSqlServer
                            ? "SQL Server has no declared portable nested-WITH-inside-a-CTE-definition contract in the Core target profile, so this shape fails closed."
                            : "Firebird CTE-definition-local WITH remains fail-closed until a target-profile contract is modeled and integration-tested."),
            new("select.cte_scope", "query", SqlCapabilityStatus.Rejected,
                "Scalar/EXISTS root CTE set queries that require an outer ORDER BY/LIMIT/OFFSET wrapper remain fail-closed because the generated derived wrapper can break correlated outer-reference scope. Provider-specific nested-WITH support is declared separately by select.cte_derived, select.cte_set_branch, select.cte_scalar_root, and select.cte_definition_local."),
            new("expression.arithmetic", "expression", SqlCapabilityStatus.Translated,
                "+, -, *, and / are preserved by the AST/compiler."),
            new("expression.modulo", "expression",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? "Canonical modulo is rendered as MOD(left, right); source-dialect validation rejects a native % spelling where that spelling is invalid."
                    : "The provider-native % operator is emitted."),
            new("expression.concat", "expression",
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider == SqlAgentToolType.MySQL
                    ? "Canonical string concatenation is translated to CONCAT(left, right). Raw MySQL source || is accepted as concatenation only when the separate source capability profile declares PIPES_AS_CONCAT or ANSI sql_mode; without that source-session contract it remains fail-closed because MySQL otherwise interprets || as logical OR. A target profile alone never authorizes the source spelling."
                    : provider == SqlAgentToolType.MsSqlServer
                        ? "Canonical string concatenation is translated to +."
                        : "The provider-native || operator is emitted."),
            new("expression.like_escape", "expression", SqlCapabilityStatus.Translated,
                "Explicit single-character literal LIKE ESCAPE is represented structurally and emitted for all target providers while the pattern remains parameterized. Dynamic, empty, multi-character, and control-character escape specifications fail-closed. MySQL NO_BACKSLASH_ESCAPES source requires the explicit escape contract for raw LIKE; target rendering does not rely on provider-default escape semantics."),
            new("expression.boolean_select", "expression",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? "Boolean/comparison expressions in the SELECT list are rejected; predicates remain supported."
                    : "Boolean/comparison expressions can be projected in the SELECT list."),
            new("expression.boolean_literal_source", "expression", SqlCapabilityStatus.Translated,
                "Structured Core boolean values remain canonical. Raw SQL Server source rejects bare TRUE/FALSE before AST canonicalization because T-SQL bit constants use 0/1 and Core does not reinterpret those bare tokens as identifiers; quoted identifiers and numeric bit predicates remain available."),
            new("expression.cast", "expression", SqlCapabilityStatus.Translated,
                "Standard CAST input is normalized through a source-aware Core type model before provider-specific CAST spelling is emitted. Raw PostgreSQL :: cast spelling is accepted only when the declared source dialect is PostgreSQL; non-PostgreSQL raw sources fail before AST canonicalization. Unknown cross-dialect vendor types fail closed."),
            new("expression.interval", "expression",
                provider == SqlAgentToolType.Postgres ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                provider == SqlAgentToolType.Postgres
                    ? "PostgreSQL INTERVAL 'literal' is preserved. Raw Core SQL accepts this PostgreSQL-style interval literal only when the declared source dialect is PostgreSQL; structured Core input is independent of the raw source-syntax gate."
                    : "PostgreSQL-style INTERVAL 'literal' has no declared target equivalent for this provider. Raw SQL that parses into this Core interval-literal shape is also rejected when the declared source dialect is non-PostgreSQL; provider-native interval forms such as MySQL INTERVAL expr unit require a separate structured translation contract."),
            new("aggregate.string", "aggregate", SqlCapabilityStatus.Translated,
                "Portable string aggregation canonicalizes STRING_AGG/GROUP_CONCAT/LISTAGG/LIST to one value expression plus a literal separator and lowers to provider-native syntax. MySQL targets use GROUP_CONCAT(value SEPARATOR separator); raw MySQL comma-separated GROUP_CONCAT arguments remain multiple value expressions and are never reinterpreted as a separator."),
            new("aggregate.string.dynamic_separator", "aggregate", SqlCapabilityStatus.Rejected,
                "Dynamic or per-row string-aggregate separators are rejected at the Core capability boundary; the portable aggregate currently requires a literal separator so provider delimiter evaluation rules cannot drift during lowering."),
            new("temporal.typed_literals", "temporal", SqlCapabilityStatus.Translated,
                "Structured Core DATE, TIME, and TIMESTAMP values are represented as typed temporal values and bound as provider parameters. Raw typed-literal spelling is source-profiled before AST canonicalization: PostgreSQL accepts the modeled basic and WITH/WITHOUT TIME ZONE forms; MySQL accepts the basic forms but not WITH/WITHOUT TIME ZONE qualifiers; SQLite rejects ANSI typed-literal spelling; Oracle accepts DATE and TIMESTAMP basic spelling but not standalone TIME or the Core TIMESTAMP WITH/WITHOUT TIME ZONE spelling; Firebird accepts basic DATE/TIME/TIMESTAMP spelling, with zone information carried inside the literal value rather than a WITH/WITHOUT TIME ZONE type qualifier; SQL Server rejects ANSI typed-literal spelling and uses string values with CAST/CONVERT instead."),
            new("temporal.standalone_time", "temporal",
                provider == SqlAgentToolType.Oracle ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider == SqlAgentToolType.Oracle
                    ? "Oracle has no standalone TIME type; standalone TIME values are rejected."
                    : "TIME values are bound using the provider's native temporal parameter type."),
            new("temporal.offset_timestamp", "temporal",
                provider == SqlAgentToolType.MySQL ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider == SqlAgentToolType.MySQL
                    ? "MySQL has no native timestamp type that preserves an input UTC offset; offset values are rejected."
                    : "Offset timestamps are bound natively; PostgreSQL and Firebird normalize the represented instant to UTC."),
            new("temporal.current_keywords", "temporal",
                provider == SqlAgentToolType.Oracle ? SqlCapabilityStatus.Translated : SqlCapabilityStatus.Supported,
                provider == SqlAgentToolType.Oracle
                    ? "CURRENT_DATE and CURRENT_TIMESTAMP are supported; CURRENT_TIME is rejected because Oracle has no standalone TIME type."
                    : "CURRENT_DATE, CURRENT_TIME, and CURRENT_TIMESTAMP are emitted with provider-specific translation where needed."),
            new("temporal.date_arithmetic", "temporal", SqlCapabilityStatus.Translated,
                "Raw SQL DATEADD/DATEDIFF input is accepted only in declared source-dialect forms, while structured Core input can use the portable date-arithmetic shapes independently of source-native syntax. Cross-dialect semantics and target-specific unit restrictions are validated before lowering."),
            new("temporal.date_format", "temporal",
                provider == SqlAgentToolType.Firebird ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider == SqlAgentToolType.Firebird
                    ? "Portable date formatting is rejected because no complete translation is declared."
                    : "Declared source date-format functions and tokens are normalized and translated to provider-native syntax."),
            new("temporal.formatted_parse", "temporal",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? "TO_DATE input and format tokens are translated to the provider-native function."
                    : "Formatted date parsing is rejected because no complete provider translation is declared."),
            new("json.extract", "json",
                provider is SqlAgentToolType.Firebird or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle
                    ? "Ambiguous JSON_EXTRACT is rejected because the scalar/object result type is unknown; use an explicit JSON_VALUE or JSON_QUERY contract."
                    : provider == SqlAgentToolType.Firebird
                        ? "Portable JSON extraction has no declared Firebird equivalent."
                        : "Constant JSON property-chain paths such as $.user.name are normalized and translated; root-only, array-index, wildcard, filter, quoted-property, recursive-descent, and dynamic paths fail closed."),
            new("json.path.simple", "json", SqlCapabilityStatus.Translated,
                "Portable JSON paths are limited to constant property chains beginning at $, for example $.user.name; root-only, array-index, wildcard, filter, quoted property names, recursive descent, and dynamic paths are rejected before lowering."),
            new("json.set", "json",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                    ? "Portable JSON mutation has no declared equivalent for this provider."
                    : "Portable JSON mutation is rendered with provider-native functions after constant property-chain path validation."),
            new("regex.match", "regex",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    || sqlServerRegexEnabled
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? "REGEXP_LIKE semantics are rendered using the provider's declared regex syntax."
                    : provider == SqlAgentToolType.MsSqlServer && sqlServerRegexEnabled
                        ? "SQL Server REGEXP_LIKE is enabled by the declared target capability profile at compatibility level 170 or above and is emitted natively."
                        : provider == SqlAgentToolType.MsSqlServer
                            ? "SQL Server REGEXP_LIKE requires a declared target capability profile with compatibility level 170 or above; absent or lower compatibility profiles remain fail-closed."
                            : "Regex matching is rejected because no reliable native equivalent is declared."),
            new("window.basic", "window", SqlCapabilityStatus.Translated,
                "OVER with PARTITION BY and ORDER BY is represented structurally; provider-specific function/order requirements are validated before lowering."),
            new("window.frame", "window", SqlCapabilityStatus.Translated,
                "ROWS/RANGE frames are represented structurally; provider/function combinations that do not accept a frame and SQL Server RANGE offsets fail closed before lowering."),
            new("ordering.ordinal", "ordering", SqlCapabilityStatus.Translated,
                "Statement-level ORDER BY output positions are represented as typed ordinals and emitted as ordinals rather than parameterized numeric literals."),
            new("ordering.nulls", "ordering",
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Supported,
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? "Structured ASC NULLS FIRST and DESC NULLS LAST are canonicalized to the provider's identical native default ordering and the unsupported modifier is omitted. ASC NULLS LAST and DESC NULLS FIRST remain fail-closed because no single-evaluation rewrite is modeled; raw MySQL/SQL Server source syntax with NULLS modifiers is rejected at the source-dialect boundary."
                    : "NULLS FIRST/LAST is emitted natively."),
            new("parameter.unbound", "parameter", SqlCapabilityStatus.Rejected,
                "Unbound ?, :name, @name, $1, and {{name}} parameters are rejected; Custom Tool parameters are rendered first."),
            new("dml.basic", "dml", SqlCapabilityStatus.Translated,
                "INSERT VALUES, UPDATE, and DELETE use the structured DML path."),
            new("dml.update_expression", "dml", SqlCapabilityStatus.Translated,
                "UPDATE SET accepts structured scalar Core expressions including column arithmetic, scalar functions, CASE, CAST, and scalar subqueries. Aggregate/window placement and provider-specific expression capabilities are validated before lowering; runtime values remain parameters."),
            new("dml.update.boolean_assignment", "dml",
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Translated,
                provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                    ? "Definitely boolean UPDATE assignment expressions are rejected because the current Core target profile does not model a portable scalar SQL boolean for this provider."
                    : "Definitely boolean UPDATE assignment expressions use the provider's scalar boolean/value semantics."),
            new("dml.insert_select", "dml", SqlCapabilityStatus.Translated,
                "INSERT ... SELECT is supported when the source projection width is statically known and matches the target column count. CTE-free sources use SqlKata's structured insert-query path; statement-root CTE sources use the Core provider-aware CTE placement path."),
            new("dml.insert_select.cte_scope", "dml", SqlCapabilityStatus.Translated,
                "Statement-root CTEs in INSERT ... SELECT are lowered with provider-aware placement across all declared target providers while preserving parameter bindings, including root CTE set queries with outer ORDER BY/LIMIT/OFFSET. A root CTE whose body declares a local WITH follows select.cte_definition_local; nested derived/set-branch CTE support follows dml.nested_cte_scope."),
            new("dml.nested_cte_scope", "dml",
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? "DML nested SELECTs preserve CTE scope in four modeled forms: scalar/EXISTS root CTEs use a complete provider compile; CTE-definition-local WITH bodies, including local set tails, are recursively compiled and reattached as raw CTE components; derived-table and set-branch CTE fragments use the Core query-graph adapter. Ordered bindings and correlated outer references remain structural; scalar/EXISTS root CTE set queries requiring an outer tail remain fail-closed under select.cte_scope."
                    : provider == SqlAgentToolType.Oracle
                        ? "Oracle nested parenthesized or nested-definition WITH forms fail closed in DML because the target grammar rejects them; statement-root INSERT ... SELECT CTEs remain supported through the dedicated placement path."
                        : "Nested WITH fragments in DML fail closed because this provider has no declared portable general-subquery or nested-CTE-definition contract; statement-root INSERT ... SELECT CTEs remain supported through the dedicated placement path."),
            new("dml.advanced", "dml", SqlCapabilityStatus.Rejected,
                "RETURNING/OUTPUT, UPSERT/ON CONFLICT/ON DUPLICATE KEY, and MERGE are not yet in the portable DML grammar; INSERT ... SELECT is tracked separately and supported."),
            new("dml.returning_output", "dml", SqlCapabilityStatus.Rejected,
                "RETURNING and OUTPUT result clauses are not yet represented by the portable DML AST."),
            new("dml.upsert_merge", "dml", SqlCapabilityStatus.Rejected,
                "UPSERT dialect forms and MERGE are not yet represented by the portable DML AST.")
        };

        return new ProviderSqlCapabilities(Version, provider, capabilities);
    }
}
