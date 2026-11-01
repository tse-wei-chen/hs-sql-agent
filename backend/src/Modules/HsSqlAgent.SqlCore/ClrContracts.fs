
namespace HsSqlAgent.SqlCore.Enums

type SqlAgentToolType =
    | Sqlite = 0
    | Postgres = 1
    | MySQL = 2
    | MsSqlServer = 3
    | Oracle = 4
    | Firebird = 5

type ArithmeticOperator =
    | Add = 0
    | Subtract = 1
    | Multiply = 2
    | Divide = 3
    | Modulo = 4
    | Concat = 5
    | Equal = 6
    | NotEqual = 7
    | GreaterThan = 8
    | LessThan = 9
    | GreaterThanOrEqual = 10
    | LessThanOrEqual = 11
    | And = 12
    | Or = 13

type CombineType =
    | Union = 0
    | UnionAll = 1
    | Intersect = 2
    | Except = 3

type DmlOperation =
    | Insert = 0
    | Update = 1
    | Delete = 2

type JoinType =
    | Inner = 0
    | Left = 1
    | Right = 2
    | Full = 3
    | Cross = 4

type NullOrdering =
    | Default = 0
    | First = 1
    | Last = 2

type SortDirection =
    | Asc = 0
    | Desc = 1
    | Random = 2

type WindowFrameUnit =
    | Rows = 0
    | Range = 1

type WindowFrameBoundKind =
    | UnboundedPreceding = 0
    | Preceding = 1
    | CurrentRow = 2
    | Following = 3
    | UnboundedFollowing = 4

namespace HsSqlAgent.SqlCore.Core.Compilation

open System
open System.Data
open System.Data.Common
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Enums

type SqlStatementKind =
    | Query = 0
    | Select = 0
    | Insert = 1
    | Update = 2
    | Delete = 3
    | Merge = 4

type SqlDiagnosticStage =
    | Lexical = 0
    | Parse = 1
    | Binding = 2
    | SourceValidation = 3
    | SemanticValidation = 4
    | TargetCapability = 5
    | Policy = 6
    | RenderingInvariant = 7

type SqlDiagnosticCategory =
    | Syntax = 0
    | DialectSyntax = 1
    | Binding = 2
    | Capability = 3
    | Semantic = 4
    | Policy = 5
    | Invariant = 6
    | Resource = 7

[<Sealed>]
type SqlDiagnosticSpan(start: int, length: int) =
    do
        if start < 0 then invalidArg (nameof start) "Diagnostic span start must be non-negative."
        if length < 0 then invalidArg (nameof length) "Diagnostic span length must be non-negative."
    member _.Start = start
    member _.Length = length
    member _.End = start + length

[<Sealed>]
type SqlDiagnostic(
    code: string,
    stage: SqlDiagnosticStage,
    category: SqlDiagnosticCategory,
    message: string,
    span: SqlDiagnosticSpan | null) =
    do
        if String.IsNullOrWhiteSpace(code) then invalidArg (nameof code) "Diagnostic code cannot be empty."
        if String.IsNullOrWhiteSpace(message) then invalidArg (nameof message) "Diagnostic message cannot be empty."
    member _.Code = code
    member _.Stage = stage
    member _.Category = category
    member _.Message = message
    member _.Span = span

[<Sealed>]
type SqlParameterValue(name: string, value: obj | null) =
    member _.Name = name
    member _.Value = value

type SqlCompileVerdict =
    | Translated = 0
    | Rejected = 1

type SqlCompileDecisionBoundary =
    | Completed = 0
    | InputValidation = 1
    | Lexical = 2
    | Parse = 3
    | Binding = 4
    | SourceValidation = 5
    | SemanticValidation = 6
    | TargetCapability = 7
    | Policy = 8
    | RenderingInvariant = 9

[<Sealed>]
type SqlDeterminismInspectionFacts(
    parseFingerprint: string,
    canonicalFingerprint: string,
    renormalizedCanonicalFingerprint: string,
    normalizationIdempotent: bool) =
    do
        if String.IsNullOrWhiteSpace(parseFingerprint) then invalidArg (nameof parseFingerprint) "Parse fingerprint cannot be empty."
        if String.IsNullOrWhiteSpace(canonicalFingerprint) then invalidArg (nameof canonicalFingerprint) "Canonical fingerprint cannot be empty."
        if String.IsNullOrWhiteSpace(renormalizedCanonicalFingerprint) then invalidArg (nameof renormalizedCanonicalFingerprint) "Renormalized canonical fingerprint cannot be empty."
    member _.ParseFingerprint = parseFingerprint
    member _.CanonicalFingerprint = canonicalFingerprint
    member _.RenormalizedCanonicalFingerprint = renormalizedCanonicalFingerprint
    member _.NormalizationIdempotent = normalizationIdempotent

type SqlCompileCapabilitySide =
    | Source = 0
    | Target = 1

type SqlCompileCapabilityStatus =
    | Supported = 0
    | Translated = 1
    | Rejected = 2

[<Sealed>]
type SqlCompileSettingEvidence(name: string, value: string) =
    do
        if String.IsNullOrWhiteSpace(name) then invalidArg (nameof name) "Compile-evidence setting name cannot be empty."
    member _.Name = name
    member _.Value = value

[<Sealed>]
type SqlCompileProfileEvidence(
    provider: SqlAgentToolType,
    serverVersion: string | null,
    compatibilityLevel: Nullable<int>,
    sessionModes: ImmutableArray<string>,
    sessionSettings: ImmutableArray<SqlCompileSettingEvidence>) =
    member _.Provider = provider
    member _.ServerVersion = serverVersion
    member _.CompatibilityLevel = compatibilityLevel
    member _.SessionModes = sessionModes
    member _.SessionSettings = sessionSettings

[<Sealed>]
type SqlCompileCapabilityEvidence(
    side: SqlCompileCapabilitySide,
    id: string,
    category: string,
    status: SqlCompileCapabilityStatus,
    detail: string) =
    do
        if String.IsNullOrWhiteSpace(id) then invalidArg (nameof id) "Compile-evidence capability id cannot be empty."
    member _.Side = side
    member _.Id = id
    member _.Category = category
    member _.Status = status
    member _.Detail = detail

[<Sealed>]
type SqlCompilePolicyEvidence(
    policyVersion: string,
    queryMaxRows: int,
    requireUpdatePredicate: bool,
    requireDeletePredicate: bool,
    allowedTables: ImmutableArray<string>) =
    do
        if String.IsNullOrWhiteSpace(policyVersion) then invalidArg (nameof policyVersion) "Compile-evidence policy version cannot be empty."
        if queryMaxRows < 0 then invalidArg (nameof queryMaxRows) "Compile-evidence query row cap cannot be negative."
    member _.PolicyVersion = policyVersion
    member _.QueryMaxRows = queryMaxRows
    member _.RequireUpdatePredicate = requireUpdatePredicate
    member _.RequireDeletePredicate = requireDeletePredicate
    member _.AllowedTables = allowedTables

[<Sealed>]
type SqlCompileAssuranceEvidence(
    kind: string,
    details: ImmutableArray<SqlCompileSettingEvidence>) =
    do
        if String.IsNullOrWhiteSpace(kind) then invalidArg (nameof kind) "Compile-evidence assurance kind cannot be empty."
    member _.Kind = kind
    member _.Details = details

[<Sealed>]
type SqlCompileEvidence(
    schemaVersion: string,
    capabilityMatrixVersion: string,
    sourceProfile: SqlCompileProfileEvidence,
    targetProfile: SqlCompileProfileEvidence,
    sourceCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>,
    targetCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>,
    policy: SqlCompilePolicyEvidence,
    assurances: ImmutableArray<SqlCompileAssuranceEvidence>,
    verdict: SqlCompileVerdict,
    decisionBoundary: SqlCompileDecisionBoundary,
    decisionCode: string,
    planFingerprint: string | null,
    evidenceFingerprint: string) =
    static let evidenceDataKey = "HsSqlAgent.SqlCore.CompileEvidence"
    do
        if String.IsNullOrWhiteSpace(schemaVersion) then invalidArg (nameof schemaVersion) "Compile-evidence schema version cannot be empty."
        if String.IsNullOrWhiteSpace(capabilityMatrixVersion) then invalidArg (nameof capabilityMatrixVersion) "Compile-evidence matrix version cannot be empty."
        if Object.ReferenceEquals(sourceProfile, null) then nullArg (nameof sourceProfile)
        if Object.ReferenceEquals(targetProfile, null) then nullArg (nameof targetProfile)
        if Object.ReferenceEquals(policy, null) then nullArg (nameof policy)
        if String.IsNullOrWhiteSpace(decisionCode) then invalidArg (nameof decisionCode) "Compile-evidence decision code cannot be empty."
        if String.IsNullOrWhiteSpace(evidenceFingerprint) then invalidArg (nameof evidenceFingerprint) "Compile-evidence fingerprint cannot be empty."
    member _.SchemaVersion = schemaVersion
    member _.CapabilityMatrixVersion = capabilityMatrixVersion
    member _.SourceProfile = sourceProfile
    member _.TargetProfile = targetProfile
    member _.SourceCapabilities = sourceCapabilities
    member _.TargetCapabilities = targetCapabilities
    member _.Policy = policy
    member _.Assurances = assurances
    member _.Verdict = verdict
    member _.DecisionBoundary = decisionBoundary
    member _.DecisionCode = decisionCode
    member _.PlanFingerprint = planFingerprint
    member _.EvidenceFingerprint = evidenceFingerprint
    static member internal DataKey = evidenceDataKey
    static member TryGetFromException(error: Exception) : SqlCompileEvidence | null =
        ArgumentNullException.ThrowIfNull(error)
        match error.Data[evidenceDataKey] with
        | :? SqlCompileEvidence as evidence -> evidence
        | _ -> null

[<Sealed>]
type CompiledSqlCommand private (
    sql: string,
    parameters: ImmutableArray<SqlParameterValue>,
    kind: SqlStatementKind,
    planFingerprint: string,
    targetProvider: SqlAgentToolType,
    returnsRows: bool,
    compileEvidence: SqlCompileEvidence | null) =

    new(
        sql: string,
        parameters: ImmutableArray<SqlParameterValue>,
        kind: SqlStatementKind,
        planFingerprint: string,
        targetProvider: SqlAgentToolType) =
        CompiledSqlCommand(
            sql,
            parameters,
            kind,
            planFingerprint,
            targetProvider,
            false,
            null)

    member _.Sql = sql
    member _.Parameters = parameters
    member _.Kind = kind
    member _.PlanFingerprint = planFingerprint
    member _.TargetProvider = targetProvider
    member _.ReturnsRows = returnsRows
    member _.CompileEvidence = compileEvidence

    static member internal Create(
        sql: string,
        parameters: ImmutableArray<SqlParameterValue>,
        kind: SqlStatementKind,
        planFingerprint: string,
        targetProvider: SqlAgentToolType,
        returnsRows: bool) =
        CompiledSqlCommand(
            sql,
            parameters,
            kind,
            planFingerprint,
            targetProvider,
            returnsRows,
            null)

    static member internal Create(
        sql: string,
        parameters: ImmutableArray<SqlParameterValue>,
        kind: SqlStatementKind,
        planFingerprint: string,
        targetProvider: SqlAgentToolType,
        returnsRows: bool,
        compileEvidence: SqlCompileEvidence | null) =
        CompiledSqlCommand(
            sql,
            parameters,
            kind,
            planFingerprint,
            targetProvider,
            returnsRows,
            compileEvidence)

type SqlCompilationException(message: string, innerException: Exception | null, diagnostic: SqlDiagnostic | null) as this =
    inherit InvalidOperationException(message, innerException)
    new(message: string) = SqlCompilationException(message, null, null)
    new(message: string, innerException: Exception) = SqlCompilationException(message, innerException, null)
    new(message: string, diagnostic: SqlDiagnostic) = SqlCompilationException(message, null, diagnostic)
    member _.Diagnostic = diagnostic
    member _.CompileEvidence = SqlCompileEvidence.TryGetFromException(this)

namespace HsSqlAgent.SqlCore.SqlParsing

open System
open HsSqlAgent.SqlCore.Core.Compilation

type SqlParseException(message: string, innerException: Exception | null, diagnostic: SqlDiagnostic | null) as this =
    inherit Exception(message, innerException)
    new(message: string) = SqlParseException(message, null, null)
    new(message: string, innerException: Exception) = SqlParseException(message, innerException, null)
    new(message: string, diagnostic: SqlDiagnostic) = SqlParseException(message, null, diagnostic)
    member _.Diagnostic = diagnostic
    member _.CompileEvidence = SqlCompileEvidence.TryGetFromException(this)

namespace HsSqlAgent.SqlCore.Models

open System
open System.Collections.Generic
open System.Data
open System.Data.Common
open HsSqlAgent.SqlCore.Enums

[<Sealed>]
type SqlProviderCapabilityProfile(
    provider: SqlAgentToolType,
    serverVersion: Version | null,
    compatibilityLevel: Nullable<int>,
    sessionModes: IReadOnlySet<string> | null,
    sessionSettings: IReadOnlyDictionary<string, string> | null) =

    new(provider: SqlAgentToolType) =
        SqlProviderCapabilityProfile(provider, null, Nullable(), null, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, Nullable(), null, null)

    new(provider: SqlAgentToolType, ``CompatibilityLevel``: Nullable<int>) =
        SqlProviderCapabilityProfile(provider, null, ``CompatibilityLevel``, null, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version, ``CompatibilityLevel``: Nullable<int>) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, ``CompatibilityLevel``, null, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version, ``SessionModes``: IReadOnlySet<string>) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, Nullable(), ``SessionModes``, null)

    new(provider: SqlAgentToolType, ``ServerVersion``: Version, ``SessionModes``: IReadOnlySet<string>, ``SessionSettings``: IReadOnlyDictionary<string, string>) =
        SqlProviderCapabilityProfile(provider, ``ServerVersion``, Nullable(), ``SessionModes``, ``SessionSettings``)

    member _.Provider = provider
    member _.ServerVersion = serverVersion
    member _.CompatibilityLevel = compatibilityLevel
    member _.SessionModes = sessionModes
    member _.SessionSettings = sessionSettings

    member _.HasSessionMode(mode: string) =
        if String.IsNullOrWhiteSpace(mode) then false
        else
            match sessionModes with
            | null -> false
            | modes ->
                modes
                |> Seq.exists (fun candidate ->
                    String.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase))

    member _.GetSessionSetting(name: string) : string | null =
        if String.IsNullOrWhiteSpace(name) then null
        else
            match sessionSettings with
            | null -> null
            | settings ->
                match
                    settings
                    |> Seq.tryPick (fun pair ->
                        if String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) then Some pair.Value else None)
                with
                | Some value -> value
                | None -> null

type internal SqlProviderCapabilityProfileValidationIssue =
    | None = 0
    | ProviderMismatch = 1
    | NegativeCompatibilityLevel = 2

[<AbstractClass; Sealed>]
type internal SqlProviderCapabilityProfileRules private () =
    static member ValidationIssue(profile: SqlProviderCapabilityProfile | null, expectedProvider: SqlAgentToolType) =
        match profile with
        | null -> SqlProviderCapabilityProfileValidationIssue.None
        | value when value.Provider <> expectedProvider ->
            SqlProviderCapabilityProfileValidationIssue.ProviderMismatch
        | value when value.CompatibilityLevel.HasValue && value.CompatibilityLevel.Value < 0 ->
            SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel
        | _ -> SqlProviderCapabilityProfileValidationIssue.None

[<AbstractClass>]
type SqlTemporalValue() = class end

[<Sealed>]
type SqlDateValue() =
    inherit SqlTemporalValue()
    member val Value = DateOnly.MinValue with get, set
    new(value: DateOnly) as this = SqlDateValue() then this.Value <- value

[<Sealed>]
type SqlTimeValue() =
    inherit SqlTemporalValue()
    member val Value = TimeOnly.MinValue with get, set
    new(value: TimeOnly) as this = SqlTimeValue() then this.Value <- value

[<Sealed>]
type SqlLocalDateTimeValue() =
    inherit SqlTemporalValue()
    member val Value = DateTime.MinValue with get, set
    new(value: DateTime) as this = SqlLocalDateTimeValue() then
        this.Value <- DateTime.SpecifyKind(value, DateTimeKind.Unspecified)

[<Sealed>]
type SqlOffsetDateTimeValue() =
    inherit SqlTemporalValue()
    member val Value = DateTimeOffset.MinValue with get, set
    new(value: DateTimeOffset) as this = SqlOffsetDateTimeValue() then this.Value <- value

type BuildDbConnectionModelBase() =
    member val Host: string | null = null with get, set
    member val Port: string | null = null with get, set
    member val Username: string | null = null with get, set
    member val Password: string | null = null with get, set
    member val Database: string | null = null with get, set
    member val ExtraSettings: string | null = null with get, set

type BuildDbConnectionModel() =
    inherit BuildDbConnectionModelBase()
    member val Provider = String.Empty with get, set

type ColumnInfo() =
    let mutable name = String.Empty
    member _.Name with get() = name and set value = name <- value
    member _.Column with get() = name and set value = name <- value
    member val Type = String.Empty with get, set
    member val Description = String.Empty with get, set
    member val IsPrimaryKey = false with get, set
    member val PrimaryKeyOrdinal = Nullable<int>() with get, set
    new(name: string, typeName: string, isPrimaryKey: bool, primaryKeyOrdinal: Nullable<int>) as this =
        ColumnInfo()
        then
            this.Name <- name
            this.Type <- typeName
            this.IsPrimaryKey <- isPrimaryKey
            this.PrimaryKeyOrdinal <- primaryKeyOrdinal
    new(name: string, typeName: string) = ColumnInfo(name, typeName, false, Nullable())
    new(name: string, typeName: string, isPrimaryKey: bool) = ColumnInfo(name, typeName, isPrimaryKey, Nullable())

type SqlExecutionPolicy() =
    member val QueryMaxRows = 0 with get, set
    member val QueryTimeoutSeconds = 30 with get, set
    member val RequireWhereForUpdate = false with get, set
    member val RequireWhereForDelete = false with get, set
    member val AllowFullTableUpdate = false with get, set
    member val AllowFullTableDelete = false with get, set
    member val DmlMaxAffectedRows = 0 with get, set

type SqlCapabilityStatus =
    | Supported = 0
    | Translated = 1
    | Rejected = 2

[<Sealed>]
type SqlCapability(id: string, category: string, status: SqlCapabilityStatus, detail: string) =
    member _.Id = id
    member _.Category = category
    member _.Status = status
    member _.Detail = detail

[<Sealed>]
type ProviderSqlCapabilities(matrixVersion: string, provider: SqlAgentToolType, capabilities: IReadOnlyList<SqlCapability>) =
    member _.MatrixVersion = matrixVersion
    member _.Provider = provider
    member _.Capabilities = capabilities

namespace HsSqlAgent.SqlCore.Core.Pipeline

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Data
open System.Data.Common
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Core.Compilation

[<Sealed>]
type DmlCompilationPolicy(
    requireWhereForUpdate: bool,
    requireWhereForDelete: bool,
    allowFullTableUpdate: bool,
    allowFullTableDelete: bool) =
    new() = DmlCompilationPolicy(true, true, false, false)
    new(requireWhereForUpdate: bool) = DmlCompilationPolicy(requireWhereForUpdate, true, false, false)
    new(requireWhereForUpdate: bool, requireWhereForDelete: bool) =
        DmlCompilationPolicy(requireWhereForUpdate, requireWhereForDelete, false, false)
    new(requireWhereForUpdate: bool, requireWhereForDelete: bool, allowFullTableUpdate: bool) =
        DmlCompilationPolicy(requireWhereForUpdate, requireWhereForDelete, allowFullTableUpdate, false)
    member _.RequireWhereForUpdate = requireWhereForUpdate
    member _.RequireWhereForDelete = requireWhereForDelete
    member _.AllowFullTableUpdate = allowFullTableUpdate
    member _.AllowFullTableDelete = allowFullTableDelete

[<Sealed>]
type DmlConflictTargetAssurance(primaryKeyColumns: ImmutableArray<string>) =
    let mutable matchedUniqueKeyColumns = ImmutableArray<string>.Empty
    let mutable matchedUniqueKeyName: string | null = null
    let mutable matchedUniqueKeyIsPrimaryKey = false
    let mutable enforcedUniqueKeyCount = 0
    let mutable hasUnsupportedEnforcedUniqueKeys = false
    let mutable sourceRowsUniqueByInsertColumns = ImmutableArray<string>.Empty

    member _.PrimaryKeyColumns = primaryKeyColumns
    member _.MatchedUniqueKeyColumns with get() = matchedUniqueKeyColumns and set value = matchedUniqueKeyColumns <- value
    member _.MatchedUniqueKeyName with get() = matchedUniqueKeyName and set value = matchedUniqueKeyName <- value
    member _.MatchedUniqueKeyIsPrimaryKey with get() = matchedUniqueKeyIsPrimaryKey and set value = matchedUniqueKeyIsPrimaryKey <- value
    member _.EnforcedUniqueKeyCount with get() = enforcedUniqueKeyCount and set value = enforcedUniqueKeyCount <- value
    member _.HasUnsupportedEnforcedUniqueKeys with get() = hasUnsupportedEnforcedUniqueKeys and set value = hasUnsupportedEnforcedUniqueKeys <- value
    member _.SourceRowsUniqueByInsertColumns with get() = sourceRowsUniqueByInsertColumns and set value = sourceRowsUniqueByInsertColumns <- value
    member _.IsSoleEnforcedUniqueKey =
        not matchedUniqueKeyColumns.IsDefaultOrEmpty
        && enforcedUniqueKeyCount = 1
        && not hasUnsupportedEnforcedUniqueKeys

    static member private NormalizeColumns(columns: IEnumerable<string>, assuranceName: string, parameterName: string) =
        if Object.ReferenceEquals(columns, null) then nullArg parameterName
        let normalized =
            columns
            |> Seq.map (fun column ->
                if String.IsNullOrWhiteSpace(column) then
                    raise (ArgumentException(assuranceName + " columns cannot be empty.", parameterName))
                column.Trim())
            |> Seq.toArray
        if normalized.Length = 0 then raise (ArgumentException(assuranceName + " requires at least one column.", parameterName))
        if (normalized |> Seq.distinctBy (fun value -> value.ToUpperInvariant()) |> Seq.length) <> normalized.Length then
            raise (ArgumentException(assuranceName + " columns cannot contain duplicates.", parameterName))
        ImmutableArray.CreateRange(normalized)

    member this.WithSourceRowsUniqueByInsertColumns(columns: IEnumerable<string>) =
        let clone = DmlConflictTargetAssurance(primaryKeyColumns)
        clone.MatchedUniqueKeyColumns <- matchedUniqueKeyColumns
        clone.MatchedUniqueKeyName <- matchedUniqueKeyName
        clone.MatchedUniqueKeyIsPrimaryKey <- matchedUniqueKeyIsPrimaryKey
        clone.EnforcedUniqueKeyCount <- enforcedUniqueKeyCount
        clone.HasUnsupportedEnforcedUniqueKeys <- hasUnsupportedEnforcedUniqueKeys
        clone.SourceRowsUniqueByInsertColumns <- DmlConflictTargetAssurance.NormalizeColumns(columns, "Source-row uniqueness assurance", "columns")
        clone

    static member FromPrimaryKey(columns: IEnumerable<string>) =
        DmlConflictTargetAssurance(DmlConflictTargetAssurance.NormalizeColumns(columns, "Primary-key assurance", "columns"))

    static member FromUniqueKey(
        columns: IEnumerable<string>,
        keyName: string,
        isPrimaryKey: bool,
        enforcedUniqueKeyCount: int,
        hasUnsupportedEnforcedUniqueKeys: bool) =
        if String.IsNullOrWhiteSpace(keyName) then invalidArg "keyName" "Unique-key assurance key name cannot be empty."
        if enforcedUniqueKeyCount < 1 then
            raise (ArgumentOutOfRangeException("enforcedUniqueKeyCount", enforcedUniqueKeyCount, "Unique-key assurance requires at least one enforced unique key in the provider inventory."))
        let value = DmlConflictTargetAssurance(ImmutableArray<string>.Empty)
        value.MatchedUniqueKeyColumns <- DmlConflictTargetAssurance.NormalizeColumns(columns, "Unique-key assurance", "columns")
        value.MatchedUniqueKeyName <- keyName.Trim()
        value.MatchedUniqueKeyIsPrimaryKey <- isPrimaryKey
        value.EnforcedUniqueKeyCount <- enforcedUniqueKeyCount
        value.HasUnsupportedEnforcedUniqueKeys <- hasUnsupportedEnforcedUniqueKeys
        value

[<Sealed>]
type DmlResultRowAssurance private (targetTable: string, operation: DmlOperation) =
    member _.TargetTable = targetTable
    member _.Operation = operation

    static member NoEnabledTriggers(targetTable: string, operation: DmlOperation) =
        if String.IsNullOrWhiteSpace(targetTable) then
            invalidArg "targetTable" "DML result-row assurance requires a non-empty target table."
        DmlResultRowAssurance(targetTable.Trim(), operation)

/// Portable, opt-in type families, never provider-native SQL type declarations.
/// Unknown is intentional: name-only catalogs preserve earlier checking behavior.
type SqlSchemaValueKind =
    | Unknown = 0
    | Boolean = 1
    | Numeric = 2
    | Text = 3
    | Temporal = 4
    | Binary = 5
    | Json = 6
    /// Provider-independent 128-bit UUID family; never implicit Numeric or Text.
    | Uuid = 7

[<Sealed>]
type SqlSchemaColumn(name: string, kind: SqlSchemaValueKind) =
    do
        if String.IsNullOrWhiteSpace(name) || name <> name.Trim() then
            invalidArg "name" "Schema column names must be non-empty and trimmed."
        if not (Enum.IsDefined(kind)) then
            invalidArg "kind" "Unknown schema value-kind enum value."
    member _.Name = name
    member _.Kind = kind

/// Host-provided immutable catalog table; both typed and legacy name-only paths
/// take defensive copies, so callers cannot mutate a compilation's proof inputs.
[<Sealed>]
type SqlSchemaTable private (name: string, definitions: ImmutableArray<SqlSchemaColumn>) =
    do ArgumentException.ThrowIfNullOrWhiteSpace(name)
    do
        if name.Split('.') |> Array.exists String.IsNullOrWhiteSpace then
            invalidArg "name" "Table identifier parts must be non-empty."
        if definitions.IsDefaultOrEmpty then
            invalidArg "columns" "Schema tables require at least one column."
        if definitions |> Seq.exists (fun c -> Object.ReferenceEquals(c, null)) then
            invalidArg "columns" "Schema columns cannot be null."
        if (definitions |> Seq.map (fun c -> c.Name) |> Seq.distinct |> Seq.length) <> definitions.Length then
            invalidArg "columns" "Schema tables may not declare duplicate column names."

    let names =
        definitions |> Seq.map (fun c -> c.Name) |> ImmutableArray.CreateRange<string>

    new(name: string, columns: IEnumerable<string>) =
        SqlSchemaTable(
            name,
            if isNull (box columns) then nullArg "columns"
            else
                columns
                |> Seq.map (fun column -> SqlSchemaColumn(column, SqlSchemaValueKind.Unknown))
                |> ImmutableArray.CreateRange<SqlSchemaColumn>)

    static member WithTypes(name: string, columns: IEnumerable<SqlSchemaColumn>) =
        ArgumentNullException.ThrowIfNull(columns)
        SqlSchemaTable(name, ImmutableArray.CreateRange<SqlSchemaColumn>(columns))

    member _.Name = name
    member _.Columns : IReadOnlyList<string> = names
    member _.TypedColumns : IReadOnlyList<SqlSchemaColumn> = definitions

/// Metadata is copied on construction. Version identifies the supplied snapshot;
/// it does not imply catalog freshness or transaction-level execution guarantees.
[<Sealed>]
type SqlSchemaSnapshot(provider: SqlAgentToolType, version: string, tables: IEnumerable<SqlSchemaTable>) =
    do ArgumentException.ThrowIfNullOrWhiteSpace(version)
    do ArgumentNullException.ThrowIfNull(tables)
    let copies =
        tables
        |> Seq.map (fun item ->
            ArgumentNullException.ThrowIfNull(item)
            item)
        |> ImmutableArray.CreateRange<SqlSchemaTable>
    do
        if copies.IsEmpty then invalidArg "tables" "Schema snapshots must contain at least one table."
        if (copies |> Seq.map (fun item -> item.Name) |> Seq.distinct |> Seq.length) <> copies.Length then
            invalidArg "tables" "Schema snapshots may not declare duplicate table names."
    member _.Provider = provider
    member _.Version = version
    member _.Tables : IReadOnlyList<SqlSchemaTable> = copies

    /// Exact immutable catalog proof identity, shared by compilation and runtime hosts.
    member _.Sha256 =
        let builder = StringBuilder()
        let append (text: string) =
            builder.Append(text.Length).Append(':').Append(text).Append(';') |> ignore
        append "sqlcore-schema-v1"
        append (string provider)
        append version
        copies
        |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.Name, b.Name))
        |> Seq.iter (fun table ->
            append table.Name
            append (string table.Columns.Count)
            table.TypedColumns
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Name, right.Name))
            |> Seq.iter (fun column ->
                append column.Name
                append (string (int column.Kind))))
        builder.ToString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun digest -> digest.ToLowerInvariant()

/// Snapshot-bound evidence minted from actual SQLite PRAGMA metadata.
/// Caller must reuse an unchanged live schema through execution; the
/// compiler has no connection and cannot prove absence of later DDL.
[<Sealed>]
type SqliteUpdateFromKeyAssurance private (
    pinnedSnapshot: SqlSchemaSnapshot,
    schemaSha256: string,
    targetTable: string,
    targetPrimaryKey: string,
    sourceTable: string,
    sourcePrimaryKey: string,
    schemaVersion: int) =

    member _.SchemaSha256 = schemaSha256
    member _.TargetTable = targetTable
    member _.TargetPrimaryKey = targetPrimaryKey
    member _.SourceTable = sourceTable
    member _.SourcePrimaryKey = sourcePrimaryKey
    member _.DatabaseSchemaVersion = schemaVersion

    /// The compiler does not contact a database. Immediately before a host
    /// executes a compiled command, re-read the live PK/index metadata and
    /// SQLite schema cookie on the *execution connection*.
    member this.IsCurrent(connection: DbConnection) =
        try
            let refreshed: SqliteUpdateFromKeyAssurance = SqliteUpdateFromKeyAssurance.VerifyLive(
                connection, pinnedSnapshot, targetTable, targetPrimaryKey,
                sourceTable, sourcePrimaryKey)
            refreshed.DatabaseSchemaVersion = schemaVersion
            && StringComparer.Ordinal.Equals(refreshed.SchemaSha256, schemaSha256)
        with
        | :? ArgumentException
        | :? InvalidOperationException
        | :? DbException -> false

    /// The signature binds the attested PK metadata to compiler evidence,
    /// independent of parameter values or SQL text.
    member _.ProofIdentity =
        String.Join("|", [| "sqlite-live-pk-v1"; schemaSha256; targetTable; targetPrimaryKey;
                            sourceTable; sourcePrimaryKey; string schemaVersion |])

    static member VerifyLive(
        connection: DbConnection, snapshot: SqlSchemaSnapshot,
        targetTable: string, targetPrimaryKey: string,
        sourceTable: string, sourcePrimaryKey: string) =

        ArgumentNullException.ThrowIfNull(connection)
        ArgumentNullException.ThrowIfNull(snapshot)
        if connection.State <> ConnectionState.Open then
            invalidArg "connection" "SQLite metadata assurance requires an open connection."
        if snapshot.Provider <> SqlAgentToolType.Sqlite then
            invalidArg "snapshot" "SQLite metadata assurance requires a SQLite schema snapshot."

        let identifier (name: string) (value: string) =
            let ascii c =
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c = '_'
            if String.IsNullOrEmpty value
               || not ((value.[0] >= 'A' && value.[0] <= 'Z')
                       || (value.[0] >= 'a' && value.[0] <= 'z') || value.[0] = '_')
               || not (value |> Seq.forall ascii) then
                invalidArg name "SQLite live PK evidence supports only simple ASCII identifiers."
            value

        let target = identifier "targetTable" targetTable
        let targetPk = identifier "targetPrimaryKey" targetPrimaryKey
        let source = identifier "sourceTable" sourceTable
        let sourcePk = identifier "sourcePrimaryKey" sourcePrimaryKey
        if String.Equals(target, source, StringComparison.OrdinalIgnoreCase) then
            invalidArg "sourceTable" "Self-joined SQLite PK assurance is outside the proven subset."

        let ensureSnapshot table key =
            let found =
                snapshot.Tables
                |> Seq.filter (fun item ->
                    String.Equals(item.Name, table, StringComparison.OrdinalIgnoreCase))
                |> Seq.toList
            match found with
            | [tableInfo] ->
                let cols =
                    tableInfo.TypedColumns
                    |> Seq.filter (fun column ->
                        String.Equals(column.Name, key, StringComparison.OrdinalIgnoreCase)
                        && column.Kind = SqlSchemaValueKind.Numeric)
                    |> Seq.length
                if cols <> 1 then
                    invalidArg "snapshot" "Live SQLite INTEGER PK must have a matching Numeric schema column."
            | _ -> invalidArg "snapshot" "Live SQLite PK table must exist exactly once in snapshot."

        let scalar sql =
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.ExecuteScalar()

        let version = Convert.ToString(scalar "SELECT sqlite_version()")
        if String.IsNullOrWhiteSpace(version)
           || Version.Parse(version).CompareTo(Version(3, 33, 0)) < 0 then
            invalidArg "connection" "SQLite 3.33+ is required for UPDATE FROM."

        let verifyPrimaryKey table key =
            use columns = connection.CreateCommand()
            columns.CommandText <- "PRAGMA table_info(" + table + ")"
            use reader = columns.ExecuteReader()
            let mutable pkCount = 0
            let mutable isExactIntegerPk = false
            while reader.Read() do
                let ordinal = Convert.ToInt32(reader.GetValue(5))
                if ordinal > 0 then
                    pkCount <- pkCount + 1
                    if ordinal = 1
                       && String.Equals(reader.GetString(1), key, StringComparison.OrdinalIgnoreCase)
                       && String.Equals(reader.GetString(2), "INTEGER", StringComparison.OrdinalIgnoreCase) then
                        isExactIntegerPk <- true
            if pkCount <> 1 || not isExactIntegerPk then
                invalidArg "connection" "SQLite PK assurance requires one exact INTEGER PRIMARY KEY column."
            // A rowid alias has no separate PK index. An ordinary indexed
            // PRIMARY KEY (including INTEGER PRIMARY KEY DESC / WITHOUT
            // ROWID) does not meet this contract.
            use indexes = connection.CreateCommand()
            indexes.CommandText <- "PRAGMA index_list(" + table + ")"
            use indexReader = indexes.ExecuteReader()
            while indexReader.Read() do
                if String.Equals(indexReader.GetString(3), "pk", StringComparison.OrdinalIgnoreCase) then
                    invalidArg "connection" "SQLite PK assurance needs a rowid-alias INTEGER PRIMARY KEY."

        ensureSnapshot target targetPk
        ensureSnapshot source sourcePk
        verifyPrimaryKey target targetPk
        verifyPrimaryKey source sourcePk
        let schemaVersion = Convert.ToInt32(scalar "PRAGMA schema_version")
        SqliteUpdateFromKeyAssurance(
            snapshot, snapshot.Sha256, target, targetPk, source, sourcePk, schemaVersion)

/// Closed physical SQL Server key kind: conversion is not a uniqueness proof.
/// The concrete physical string comparison contract must match the same
/// UNIQUE-index and equality-operand collation at every key ordinal.
type private SqlServerTextEquality =
    | BinaryCodepoint
    | CaseInsensitiveAccentSensitive
    | CaseInsensitiveAccentInsensitive
    | CaseSensitiveAccentSensitive

module private SqlServerTextEquality =
    let fromPhysical (collation: string) =
        match collation with
        | "Latin1_General_100_BIN2" -> Some BinaryCodepoint
        | "Latin1_General_100_CI_AS" -> Some CaseInsensitiveAccentSensitive
        | "Latin1_General_100_CI_AI" -> Some CaseInsensitiveAccentInsensitive
        | "Latin1_General_100_CS_AS" -> Some CaseSensitiveAccentSensitive
        | _ -> None

    let label = function
        | BinaryCodepoint -> "Latin1_General_100_BIN2"
        | CaseInsensitiveAccentSensitive -> "Latin1_General_100_CI_AS"
        | CaseInsensitiveAccentInsensitive -> "Latin1_General_100_CI_AI"
        | CaseSensitiveAccentSensitive -> "Latin1_General_100_CS_AS"

type private SqlServerExactKeyKind =
    | UInt8
    | Int16
    | Int32
    | Int64
    | FixedDecimal of byte * byte
    | Uuid
    | VarChar of int16 * SqlServerTextEquality
    | NVarChar of int16 * SqlServerTextEquality

module private SqlServerExactKeyKind =
    let fromPhysical (sqlType: string) (precision: byte) (scale: byte)
                     (maxLength: int16) (collation: string) =
        if StringComparer.OrdinalIgnoreCase.Equals(sqlType, "tinyint") then Some UInt8
        elif StringComparer.OrdinalIgnoreCase.Equals(sqlType, "smallint") then Some Int16
        elif StringComparer.OrdinalIgnoreCase.Equals(sqlType, "int") then Some Int32
        elif StringComparer.OrdinalIgnoreCase.Equals(sqlType, "bigint") then Some Int64
        elif StringComparer.OrdinalIgnoreCase.Equals(sqlType, "decimal")
             || StringComparer.OrdinalIgnoreCase.Equals(sqlType, "numeric") then
            // SQL Server treats each (precision,scale) pair as a distinct
            // numeric type. Numeric and decimal are SQL synonyms.
            if precision >= 1uy && precision <= 38uy && scale <= precision then
                Some(FixedDecimal(precision, scale))
            else None
        elif StringComparer.OrdinalIgnoreCase.Equals(sqlType, "uniqueidentifier") then Some Uuid
        elif StringComparer.OrdinalIgnoreCase.Equals(sqlType, "varchar")
             && maxLength >= 1s && maxLength <= 8000s then
            SqlServerTextEquality.fromPhysical collation
            |> Option.map (fun equality -> VarChar(maxLength, equality))
        elif StringComparer.OrdinalIgnoreCase.Equals(sqlType, "nvarchar")
             && maxLength >= 2s && maxLength <= 8000s && maxLength % 2s = 0s then
            SqlServerTextEquality.fromPhysical collation
            |> Option.map (fun equality -> NVarChar(maxLength / 2s, equality))
        else None

    let label = function
        | UInt8 -> "TINYINT"
        | Int16 -> "SMALLINT"
        | Int32 -> "INT"
        | Int64 -> "BIGINT"
        | FixedDecimal(precision, scale) ->
            "DECIMAL(" + string (int precision) + "," + string (int scale) + ")"
        | Uuid -> "UNIQUEIDENTIFIER"
        | VarChar(n, equality) ->
            "VARCHAR(" + string (int n) + ")" +
            (if equality = BinaryCodepoint then ""
             else "@" + SqlServerTextEquality.label equality)
        | NVarChar(n, equality) ->
            "NVARCHAR(" + string (int n) + ")" +
            (if equality = BinaryCodepoint then ""
             else "@" + SqlServerTextEquality.label equality)

    /// A typed snapshot describes its actual value family. An externally
    /// declared Numeric column can never authorize physical UNIQUEIDENTIFIER.
    let matchesSnapshot kind declared =
        match kind, declared with
        | Uuid, SqlSchemaValueKind.Uuid -> true
        | (VarChar _ | NVarChar _), SqlSchemaValueKind.Text -> true
        | (UInt8 | Int16 | Int32 | Int64 | FixedDecimal _), SqlSchemaValueKind.Numeric -> true
        | _ -> false

/// Only real sys.* evidence can create this ordered physical name/type tuple.
type private SqlServerExactKeyTuple =
    private SqlServerExactKeyTuple of (string * SqlServerExactKeyKind) array

module private SqlServerExactKeyTuple =
    let ofAttested (columns: string array) (kinds: SqlServerExactKeyKind array) =
        if columns.Length <> kinds.Length then
            invalidArg "kinds" "The attested source/target key ordinal must bind one physical SQL kind."
        SqlServerExactKeyTuple(Array.zip columns kinds)

    let values (SqlServerExactKeyTuple pairs) = pairs |> Array.map fst
    let kinds (SqlServerExactKeyTuple pairs) = pairs |> Array.map snd

/// Nullable physical UNIQUE keys have a distinct, closed comparison witness.
/// A UNIQUE index proves the complete key tuple while ordinary '=' with
/// ANSI_NULLS ON makes every tuple containing NULL nonmatching.
type private SqlServerUniqueNullSemantics =
    | AllKeysNotNull
    | NullableKeysOrdinaryAnsiEquality

/// SQL Server source-key evidence is minted from its live sys.* catalog,
/// never from a caller-supplied Virtual Catalog, schema name or SQL text.
/// An explicit transaction must retain schema locks until the caller commits.
[<Sealed>]
type SqlServerCteUniqueKeyAssurance private (
    pinnedSnapshot: SqlSchemaSnapshot,
    targetTable: string,
    sourceTable: string,
    sourceKeyTuple: SqlServerExactKeyTuple,
    sourceNullSemantics: SqlServerUniqueNullSemantics,
    targetKeyTuple: SqlServerExactKeyTuple option,
    databaseId: int,
    serverInstanceName: string,
    databaseName: string,
    databaseGuid: Guid,
    sourceObjectId: int,
    targetObjectId: int,
    uniqueIndexId: int,
    uniqueIndexName: string,
    metadataDigest: string,
    joinedProof: SqlServerCteUniqueKeyAssurance option) =
    let sourceKeyColumns = SqlServerExactKeyTuple.values sourceKeyTuple
    let sourceKeyKinds = SqlServerExactKeyTuple.kinds sourceKeyTuple
    let targetKeyColumns =
        targetKeyTuple |> Option.map SqlServerExactKeyTuple.values
        |> Option.defaultValue [||]
    let targetKeyKinds =
        targetKeyTuple |> Option.map SqlServerExactKeyTuple.kinds
        |> Option.defaultValue [||]

    member _.SchemaSha256 = pinnedSnapshot.Sha256
    member _.TargetTable = targetTable
    member _.SourceTable = sourceTable
    member _.SourceKeyColumn = sourceKeyColumns.[0]
    member _.TargetKeyColumn = if targetKeyColumns.Length = 0 then null else targetKeyColumns.[0]
    member _.SourceKeyColumns = Array.AsReadOnly(sourceKeyColumns)
    member _.TargetKeyColumns = Array.AsReadOnly(targetKeyColumns)
    member _.DatabaseId = databaseId
    member _.ServerInstanceName = serverInstanceName
    member _.DatabaseName = databaseName
    member _.DatabaseGuid = databaseGuid
    member _.UniqueIndexName = uniqueIndexName
    /// The secondary physical table has its own independently minted
    /// unique-index evidence, never a virtual/catalog-only declaration.
    member _.JoinedSourceTable =
        joinedProof |> Option.map (fun proof -> proof.SourceTable)
        |> Option.defaultValue null
    member _.JoinedSourceKeyColumns =
        match joinedProof with
        | Some proof -> proof.SourceKeyColumns
        | None -> Array.AsReadOnly([||] : string array)
    member _.JoinedSourceProofIdentity =
        joinedProof |> Option.map (fun proof -> proof.ProofIdentity)
        |> Option.defaultValue null
    member _.ProofIdentity =
        String.Join("|", [| "sqlserver-live-exact-key-v9"; pinnedSnapshot.Sha256;
            serverInstanceName; databaseName; databaseGuid.ToString("D");
            string databaseId; string sourceObjectId; string targetObjectId;
            sourceTable; targetTable; sourceKeyColumns.[0];
            string uniqueIndexId; uniqueIndexName; metadataDigest |])
        + (if sourceKeyColumns.Length > 1
           then "|ordered-source-keys=" + String.Join(",", sourceKeyColumns)
           else "")
        + "|source-key-types=" + String.Join(",", sourceKeyKinds |> Array.map SqlServerExactKeyKind.label)
        + "|source-key-null-semantics=" +
          (match sourceNullSemantics with
           | AllKeysNotNull -> "not-null"
           | NullableKeysOrdinaryAnsiEquality -> "ordinary-ansi-on")
        + (if targetKeyColumns.Length = 0 then ""
           elif targetKeyColumns.Length = 1 then "|target-key=" + targetKeyColumns.[0]
           else "|ordered-target-keys=" + String.Join(",", targetKeyColumns))
        + "|target-key-types=" + String.Join(",", targetKeyKinds |> Array.map SqlServerExactKeyKind.label)
        + (match joinedProof with
           | Some dependent -> "|m07-joined-source=" + dependent.ProofIdentity
           | None -> "")

    /// Binding a proof to a key also requires an exact schema.table scope.
    /// Restricted ASCII identifiers avoid provider-specific collation and
    /// delimiter ambiguity in this first version of the proof contract.
    static member private Name (arg: string) (value: string) =
        if String.IsNullOrEmpty(value)
           || not (value |> Seq.mapi (fun i c ->
               if i = 0 then
                   (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c = '_'
               else
                   (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9') || c = '_') |> Seq.forall id) then
            invalidArg arg "SQL Server live unique-key evidence requires a simple ASCII identifier."
        value

    static member private Parts (arg: string) (value: string) =
        if isNull value then nullArg arg
        match value.Split('.') with
        | [|schema; table|] ->
            SqlServerCteUniqueKeyAssurance.Name arg schema,
            SqlServerCteUniqueKeyAssurance.Name arg table
        | _ -> invalidArg arg "SQL Server live unique-key proof requires schema.table."

    static member private Command (connection: DbConnection) (tx: DbTransaction | null) sql =
        let command = connection.CreateCommand()
        command.Transaction <- tx
        command.CommandText <- sql
        command

    static member private Bind (command: DbCommand) name (value: obj) =
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.Value <- value
        command.Parameters.Add(parameter) |> ignore

    static member private SnapshotHasTable (snapshot: SqlSchemaSnapshot) table =
        snapshot.Tables
        |> Seq.filter (fun item -> StringComparer.Ordinal.Equals(item.Name, table))
        |> Seq.length = 1

    static member private Metadata
        (connection: DbConnection, tx: DbTransaction | null,
         tableName: string, digest: IncrementalHash) =
        let schema, table = SqlServerCteUniqueKeyAssurance.Parts "tableName" tableName
        use tableCommand =
            SqlServerCteUniqueKeyAssurance.Command connection tx (
                """SELECT t.object_id, s.name, t.name
                   FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                   WHERE s.name = @schema AND t.name = @table""")
        SqlServerCteUniqueKeyAssurance.Bind tableCommand "@schema" schema
        SqlServerCteUniqueKeyAssurance.Bind tableCommand "@table" table
        let objectId =
            use reader = tableCommand.ExecuteReader()
            if not (reader.Read()) then
                invalidOp ("SQL Server physical table is absent: " + tableName)
            let id = reader.GetInt32(0)
            if not (StringComparer.Ordinal.Equals(reader.GetString(1), schema))
               || not (StringComparer.Ordinal.Equals(reader.GetString(2), table))
               || reader.Read() then
                invalidOp "SQL Server live table identity is ambiguous under database collation."
            id

        // Hash every physical column and index property needed to detect
        // a stale index/layout between preview and the commit transaction.
        // A single-key metadata proof cannot be reused after DDL drift.
        let append (value: string) =
            let bytes = Encoding.UTF8.GetBytes value
            digest.AppendData(BitConverter.GetBytes(bytes.Length))
            digest.AppendData(bytes)
        let scan sql =
            use cmd = SqlServerCteUniqueKeyAssurance.Command connection tx sql
            SqlServerCteUniqueKeyAssurance.Bind cmd "@objectId" (box objectId)
            use reader = cmd.ExecuteReader()
            while reader.Read() do
                append "row"
                for i = 0 to reader.FieldCount - 1 do
                    let value =
                        if reader.IsDBNull i then "null"
                        else
                            let item = reader.GetValue(i)
                            let text = Convert.ToString(item, Globalization.CultureInfo.InvariantCulture)
                            "v:" + (if isNull text then "" else text)
                    append value
        append tableName
        append (string objectId)
        scan """SELECT c.column_id, c.name, typ.name, c.max_length,
                        c.precision, c.scale, c.is_nullable, c.is_computed,
                        c.collation_name
                 FROM sys.columns c
                 JOIN sys.types typ ON typ.user_type_id = c.user_type_id
                 WHERE c.object_id = @objectId ORDER BY c.column_id"""
        append "indexes"
        scan """SELECT i.index_id, i.name, i.is_unique, i.is_disabled,
                        i.is_hypothetical, i.has_filter, i.filter_definition,
                        i.type, ic.index_column_id, ic.key_ordinal,
                        ic.is_included_column, c.name
                 FROM sys.indexes i
                 LEFT JOIN sys.index_columns ic
                   ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                 LEFT JOIN sys.columns c
                   ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE i.object_id = @objectId
                 ORDER BY i.index_id, ic.index_column_id"""
        objectId

    static member private Attest
        (connection: DbConnection, tx: DbTransaction | null,
         snapshot: SqlSchemaSnapshot, targetTable: string,
         sourceTable: string, sourceKeys: string array,
         targetKeys: string array, pin: bool) =
        if connection.State <> ConnectionState.Open then
            invalidArg "connection" "SQL Server live key proof requires an open connection."
        if snapshot.Provider <> SqlAgentToolType.MsSqlServer then
            invalidArg "snapshot" "Only SQL Server schema snapshots can attest SQL Server unique keys."
        if not (SqlServerCteUniqueKeyAssurance.SnapshotHasTable snapshot targetTable)
           || not (SqlServerCteUniqueKeyAssurance.SnapshotHasTable snapshot sourceTable) then
            invalidArg "snapshot" "Both physical target and source must be present in the pinned snapshot."
        if sourceKeys.Length < 1 || sourceKeys.Length > 2
           || (targetKeys.Length <> 0 && targetKeys.Length <> sourceKeys.Length)
           || (sourceKeys |> Array.distinct |> Array.length) <> sourceKeys.Length
           || (targetKeys |> Array.distinct |> Array.length) <> targetKeys.Length then
            invalidArg "sourceKeys" "SQL Server proof requires an ordered distinct 1-2 component exact key tuple."
        sourceKeys |> Array.iter (fun key -> SqlServerCteUniqueKeyAssurance.Name "sourceKeys" key |> ignore)
        targetKeys |> Array.iter (fun key -> SqlServerCteUniqueKeyAssurance.Name "targetKeys" key |> ignore)
        let sourceSnapshotKinds =
            snapshot.Tables
            |> Seq.filter (fun item -> StringComparer.Ordinal.Equals(item.Name, sourceTable))
            |> Seq.collect (fun item -> item.TypedColumns)
            |> Seq.map (fun col -> col.Name, col.Kind)
            |> Map.ofSeq
        if sourceKeys |> Array.exists (fun key -> not (sourceSnapshotKinds.ContainsKey key)) then
            invalidArg "snapshot" "Every source key requires an explicit typed column in the immutable schema snapshot."
        if pin then
            // TABLE S lock + HOLDLOCK lasts for the SAME explicit transaction;
            // DDL requires Sch-M and is blocked until commit or rollback.
            // Lock in canonical order to avoid reversed source/target order.
            for tableName in [targetTable; sourceTable] |> List.sort do
                let schema, table = SqlServerCteUniqueKeyAssurance.Parts "tableName" tableName
                use lockCommand =
                    SqlServerCteUniqueKeyAssurance.Command connection tx (
                        "SELECT TOP (1) 1 FROM [" + schema + "].[" + table + "] WITH (TABLOCK, HOLDLOCK)")
                lockCommand.ExecuteScalar() |> ignore
        // Local database and object IDs can be reused by another instance.
        // Require an actual physical database GUID and server identity.
        use databaseCommand =
            SqlServerCteUniqueKeyAssurance.Command connection tx (
                """SELECT DB_ID(), DB_NAME(),
                          CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')),
                          database_guid
                   FROM sys.database_recovery_status WHERE database_id = DB_ID()""")
        use identity = databaseCommand.ExecuteReader()
        if not (identity.Read()) || identity.IsDBNull(0)
           || identity.IsDBNull(1) || identity.IsDBNull(2)
           || identity.IsDBNull(3) then
            invalidOp "SQL Server physical database identity is not visible."
        let databaseId = Convert.ToInt32(identity.GetValue(0), Globalization.CultureInfo.InvariantCulture)
        let databaseName = identity.GetString(1)
        let serverInstanceName = identity.GetString(2)
        let databaseGuid = identity.GetGuid(3)
        if databaseId <= 0 || String.IsNullOrWhiteSpace(databaseName)
           || String.IsNullOrWhiteSpace(serverInstanceName)
           || databaseGuid = Guid.Empty || identity.Read() then
            invalidOp "SQL Server physical database identity is absent or ambiguous."
        identity.Close()
        use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let sourceId = SqlServerCteUniqueKeyAssurance.Metadata(
            connection, tx, sourceTable, digest)
        let targetId = SqlServerCteUniqueKeyAssurance.Metadata(
            connection, tx, targetTable, digest)
        // Physical target comparison must have EXACTLY the source ordinal's
        // SQL key kind and decimal precision/scale. Virtual Numeric cannot mint it.
        let targetKinds =
            targetKeys
            |> Array.map (fun key ->
                use typeCommand = SqlServerCteUniqueKeyAssurance.Command connection tx (
                    """SELECT c.name, typ.name, c.precision, c.scale,
                              c.max_length, c.collation_name,
                              c.is_computed, c.is_nullable
                       FROM sys.columns c JOIN sys.types typ
                         ON typ.user_type_id = c.user_type_id
                       WHERE c.object_id = @objectId AND c.name = @key
                         AND typ.is_user_defined = 0
                         AND typ.user_type_id = typ.system_type_id""")
                SqlServerCteUniqueKeyAssurance.Bind typeCommand "@objectId" (box targetId)
                SqlServerCteUniqueKeyAssurance.Bind typeCommand "@key" key
                use reader = typeCommand.ExecuteReader()
                if not (reader.Read()) then invalidOp "SQL Server physical target key missing."
                let name = reader.GetString(0)
                let physicalType = reader.GetString(1)
                let precision = reader.GetByte(2)
                let scale = reader.GetByte(3)
                let maxLength = reader.GetInt16(4)
                let collation = if reader.IsDBNull(5) then "" else reader.GetString(5)
                let computed = reader.GetBoolean(6)
                let nullable = reader.GetBoolean(7)
                if not (StringComparer.Ordinal.Equals(name, key))
                   || computed || (targetKeys.Length > 1 && nullable)
                   || reader.Read() then
                    invalidOp "SQL Server target key is not an exact noncomputed physical operand."
                match SqlServerExactKeyKind.fromPhysical physicalType precision scale maxLength collation with
                | Some kind -> kind
                | None -> invalidOp "SQL Server target key must use exact built-in numeric, UUID or attested Latin1 General text equality.")
        use indexCommand =
            SqlServerCteUniqueKeyAssurance.Command connection tx (
                """SELECT i.index_id, i.name, ic.key_ordinal, c.name,
                          typ.name, c.precision, c.scale,
                          c.max_length, c.collation_name,
                          c.is_nullable, c.is_computed, typ.is_user_defined,
                          typ.user_type_id, typ.system_type_id
                   FROM sys.indexes i
                   JOIN sys.index_columns ic
                     ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                   JOIN sys.columns c ON c.object_id = ic.object_id
                     AND c.column_id = ic.column_id
                   JOIN sys.types typ ON typ.user_type_id = c.user_type_id
                   WHERE i.object_id = @objectId
                     AND i.is_unique = 1 AND i.is_disabled = 0
                     AND i.is_hypothetical = 0 AND i.has_filter = 0
                     AND i.type IN (1, 2) AND ic.key_ordinal > 0
                   ORDER BY i.index_id, ic.key_ordinal""")
        SqlServerCteUniqueKeyAssurance.Bind indexCommand "@objectId" (box sourceId)
        // Filter only AFTER grouping EVERY physical key ordinal. Removing
        // unsupported alias rows in SQL could turn UNIQUE(a,b) into a
        // forged UNIQUE(a) claim when b uses a user-defined type.
        let rows = ResizeArray<int * string * int * string * string *
                               byte * byte * int16 * string * bool * bool * bool>()
        use indexes = indexCommand.ExecuteReader()
        while indexes.Read() do
            let builtin = not (indexes.GetBoolean(11))
                          && indexes.GetInt32(12) = int (indexes.GetByte(13))
            let collation = if indexes.IsDBNull(8) then "" else indexes.GetString(8)
            rows.Add(indexes.GetInt32(0), indexes.GetString(1),
                     Convert.ToInt32(indexes.GetValue(2)),
                     indexes.GetString(3), indexes.GetString(4),
                     indexes.GetByte(5), indexes.GetByte(6), indexes.GetInt16(7),
                     collation, indexes.GetBoolean(9), indexes.GetBoolean(10), builtin)
        // This reader must be released before the nullable session's
        // @@OPTIONS scalar query (SQL Server MARS is intentionally not required).
        indexes.Close()
        let matching =
            rows
            |> Seq.groupBy (fun (id, _, _, _, _, _, _, _, _, _, _, _) -> id)
            |> Seq.sortBy fst
            |> Seq.tryPick (fun (_, candidates) ->
                let keys =
                    candidates
                    |> Seq.sortBy (fun (_, _, ordinal, _, _, _, _, _, _, _, _, _) -> ordinal)
                    |> Seq.toArray
                let kinds =
                    keys |> Array.map (fun (_, _, _, _, sqlType, precision, scale,
                                            length, collation, _, _, builtin) ->
                        if builtin then
                            SqlServerExactKeyKind.fromPhysical sqlType precision scale length collation
                        else None)
                if keys.Length = sourceKeys.Length
                   && (kinds |> Array.forall Option.isSome)
                   && Array.forall2 (fun (_, _, ordinal, name, _, _, _, _, _, _, computed, builtin) key ->
                       ordinal > 0 && builtin && StringComparer.Ordinal.Equals(name, key)
                       && not computed) keys sourceKeys
                   && (targetKinds.Length = 0
                       || Array.forall2 (=) targetKinds (kinds |> Array.map Option.get)) then
                    let (id, indexName, _, _, _, _, _, _, _, _, _, _) = keys.[0]
                    let hasNullableKey =
                        keys |> Array.exists (fun (_, _, _, _, _, _, _, _, _, nullable, _, _) -> nullable)
                    Some(id, indexName, kinds |> Array.map Option.get, hasNullableKey)
                else None)
        let indexId, indexName, sourceKinds, hasNullableKey =
            match matching with
            | Some value -> value
            | None -> invalidOp "SQL Server live UNIQUE key requires complete native index ordinals, matching SQL kinds, widths and attested collation equality."
        // Nullable UNIQUE indexes are safe for the *ordinary equality* path
        // only: any NULL key component yields UNKNOWN and cannot match a
        // target. There must be a target comparison edge (not bare VerifyLive).
        // A session with legacy ANSI_NULLS OFF must never mint or revalidate
        // this expanded source proof. IsCurrent rechecks the actual commit
        // transaction's @@OPTIONS under the same held schema locks.
        let sourceNullSemantics =
            if hasNullableKey then
                if targetKeys.Length = 0 then
                    invalidOp "Nullable SQL Server source uniqueness needs an exact target equality proof."
                use ansiCommand =
                    SqlServerCteUniqueKeyAssurance.Command connection tx "SELECT @@OPTIONS"
                let sessionOptions =
                    Convert.ToInt32(ansiCommand.ExecuteScalar(), Globalization.CultureInfo.InvariantCulture)
                if (sessionOptions &&& 32) <> 32 then
                    invalidOp "Nullable UNIQUE source key requires ANSI_NULLS ON on the live session."
                NullableKeysOrdinaryAnsiEquality
            else AllKeysNotNull
        // The snapshot is an immutable type-family witness, not execution
        // authority. Require it to agree with actual sys.* key types.
        sourceKinds
        |> Array.iteri (fun ordinal kind ->
            if not (SqlServerExactKeyKind.matchesSnapshot kind sourceSnapshotKinds.[sourceKeys.[ordinal]]) then
                invalidArg "snapshot" "Source key snapshot type family differs from its live physical type.")
        let targetSnapshotKinds =
            snapshot.Tables
            |> Seq.filter (fun item -> StringComparer.Ordinal.Equals(item.Name, targetTable))
            |> Seq.collect (fun item -> item.TypedColumns)
            |> Seq.map (fun col -> col.Name, col.Kind)
            |> Map.ofSeq
        targetKinds
        |> Array.iteri (fun ordinal kind ->
            match targetSnapshotKinds.TryFind targetKeys.[ordinal] with
            | Some SqlSchemaValueKind.Unknown -> () // Legacy target name-only snapshot: physical proof remains authoritative.
            | Some declared when not (SqlServerExactKeyKind.matchesSnapshot kind declared) ->
                invalidArg "snapshot" "Target key snapshot type family differs from its live physical type."
            | _ -> ())
        let sha256 = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()
        databaseId, serverInstanceName, databaseName, databaseGuid,
        sourceId, targetId, indexId, indexName, sha256, sourceKinds, targetKinds, sourceNullSemantics

    static member VerifyLive
        (connection: DbConnection, snapshot: SqlSchemaSnapshot,
         targetTable: string, sourceTable: string, sourceKeyColumn: string) =
        ArgumentNullException.ThrowIfNull connection
        ArgumentNullException.ThrowIfNull snapshot
        SqlServerCteUniqueKeyAssurance.Parts "targetTable" targetTable |> ignore
        SqlServerCteUniqueKeyAssurance.Parts "sourceTable" sourceTable |> ignore
        SqlServerCteUniqueKeyAssurance.Name "sourceKeyColumn" sourceKeyColumn |> ignore
        if StringComparer.OrdinalIgnoreCase.Equals(targetTable, sourceTable) then
            invalidArg "sourceTable" "Self-joined live SQL Server key proof is outside the first verified subset."
        let (dbId, liveServer, liveName, liveGuid, sourceId, targetId, indexId, indexName, digest, sourceKinds, targetKinds, nullSemantics) =
            SqlServerCteUniqueKeyAssurance.Attest(
                connection, null, snapshot, targetTable, sourceTable, [|sourceKeyColumn|], [||], false)
        SqlServerCteUniqueKeyAssurance(
            snapshot, targetTable, sourceTable,
            SqlServerExactKeyTuple.ofAttested [|sourceKeyColumn|] sourceKinds, nullSemantics, None,
            dbId, liveServer, liveName, liveGuid,
            sourceId, targetId, indexId, indexName, digest, None)

    /// Only this API may mint compilation evidence for physical-read CTE
    /// UPDATE/DELETE. Both physical key equality operands are checked on the live DB.
    static member VerifyLiveForJoinedMutation
        (connection: DbConnection, snapshot: SqlSchemaSnapshot,
         targetTable: string, targetKeyColumn: string,
         sourceTable: string, sourceKeyColumn: string) =
        ArgumentNullException.ThrowIfNull connection
        ArgumentNullException.ThrowIfNull snapshot
        SqlServerCteUniqueKeyAssurance.Parts "targetTable" targetTable |> ignore
        SqlServerCteUniqueKeyAssurance.Parts "sourceTable" sourceTable |> ignore
        SqlServerCteUniqueKeyAssurance.Name "targetKeyColumn" targetKeyColumn |> ignore
        SqlServerCteUniqueKeyAssurance.Name "sourceKeyColumn" sourceKeyColumn |> ignore
        if StringComparer.OrdinalIgnoreCase.Equals(targetTable, sourceTable) then
            invalidArg "sourceTable" "Self-joined SQL Server key proof is not modeled."
        let (dbId, liveServer, liveName, liveGuid, sourceId, targetId, indexId, indexName, digest, sourceKinds, targetKinds, nullSemantics) =
            SqlServerCteUniqueKeyAssurance.Attest(
                connection, null, snapshot, targetTable, sourceTable,
                [|sourceKeyColumn|], [|targetKeyColumn|], false)
        SqlServerCteUniqueKeyAssurance(
            snapshot, targetTable, sourceTable,
            SqlServerExactKeyTuple.ofAttested [|sourceKeyColumn|] sourceKinds,
            nullSemantics,
            Some(SqlServerExactKeyTuple.ofAttested [|targetKeyColumn|] targetKinds),
            dbId, liveServer, liveName, liveGuid,
            sourceId, targetId, indexId, indexName, digest, None)

    /// Domain-owned physical proof for two ordered exact integer key ordinals.
    /// No virtual Catalog or caller DTO can mint this authority.
    static member VerifyLiveForJoinedMutation
        (connection: DbConnection, snapshot: SqlSchemaSnapshot,
         targetTable: string, targetKeys: System.Collections.Generic.IReadOnlyList<string>,
         sourceTable: string, sourceKeys: System.Collections.Generic.IReadOnlyList<string>) =
        ArgumentNullException.ThrowIfNull connection
        ArgumentNullException.ThrowIfNull snapshot
        ArgumentNullException.ThrowIfNull targetKeys
        ArgumentNullException.ThrowIfNull sourceKeys
        SqlServerCteUniqueKeyAssurance.Parts "targetTable" targetTable |> ignore
        SqlServerCteUniqueKeyAssurance.Parts "sourceTable" sourceTable |> ignore
        if StringComparer.OrdinalIgnoreCase.Equals(targetTable, sourceTable) then
            invalidArg "sourceTable" "Self-joined SQL Server key proof is not modeled."
        let target = targetKeys |> Seq.toArray
        let source = sourceKeys |> Seq.toArray
        if source.Length <> 2 || target.Length <> 2 then
            invalidArg "sourceKeys" "Composite SQL Server CTE assurance requires two ordered exact integer keys."
        let (dbId, liveServer, liveName, liveGuid, sourceId, targetId, indexId, indexName, digest, sourceKinds, targetKinds, nullSemantics) =
            SqlServerCteUniqueKeyAssurance.Attest(
                connection, null, snapshot, targetTable, sourceTable, source, target, false)
        SqlServerCteUniqueKeyAssurance(
            snapshot, targetTable, sourceTable,
            SqlServerExactKeyTuple.ofAttested source sourceKinds,
            nullSemantics,
            Some(SqlServerExactKeyTuple.ofAttested target targetKinds),
            dbId, liveServer, liveName, liveGuid,
            sourceId, targetId, indexId, indexName, digest, None)

    /// Only this live factory can combine two physical unique-index
    /// attestations. The first proof controls target matching, the second
    /// proves that a distinct JOIN source cannot multiply any source row.
    /// Both are revalidated in the actual mutation transaction.
    static member VerifyLiveForKeyPreservingJoin
        (connection: DbConnection, snapshot: SqlSchemaSnapshot,
         targetTable: string, targetKeys: System.Collections.Generic.IReadOnlyList<string>,
         sourceTable: string, sourceKeys: System.Collections.Generic.IReadOnlyList<string>,
         joinedTable: string, joinedKeys: System.Collections.Generic.IReadOnlyList<string>) =
        ArgumentNullException.ThrowIfNull connection
        ArgumentNullException.ThrowIfNull snapshot
        ArgumentNullException.ThrowIfNull targetKeys
        ArgumentNullException.ThrowIfNull sourceKeys
        ArgumentNullException.ThrowIfNull joinedKeys
        SqlServerCteUniqueKeyAssurance.Parts "joinedTable" joinedTable |> ignore
        // IReadOnlyList does not imply immutable backing storage. Freeze
        // all ordinals once before the two independent metadata reads.
        let target = targetKeys |> Seq.toArray
        let source = sourceKeys |> Seq.toArray
        let joined = joinedKeys |> Seq.toArray
        if StringComparer.OrdinalIgnoreCase.Equals(joinedTable, sourceTable)
           || StringComparer.OrdinalIgnoreCase.Equals(joinedTable, targetTable)
           || target.Length <> source.Length
           || joined.Length <> source.Length
           || source.Length < 1 || source.Length > 2 then
            invalidArg "joinedTable" "Cross-table proof needs three distinct physical tables and equal 1-2 key widths."
        let create (target: string) (keys: System.Collections.Generic.IReadOnlyList<string>)
                   (source: string) (sourceKey: System.Collections.Generic.IReadOnlyList<string>) =
            if sourceKey.Count = 1 then
                SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    connection, snapshot, target, keys.[0], source, sourceKey.[0])
            else
                SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    connection, snapshot, target, keys, source, sourceKey)
        let primary = create targetTable target sourceTable source
        // The secondary proof compares the JOINed key's exact type
        // at every ordinal against the primary physical key, not merely
        // Numeric and not an implicitly coerced SQL expression.
        let secondary = create sourceTable source joinedTable joined
        if not (StringComparer.Ordinal.Equals(primary.ServerInstanceName, secondary.ServerInstanceName))
           || primary.DatabaseGuid <> secondary.DatabaseGuid
           || primary.DatabaseId <> secondary.DatabaseId
           || not (StringComparer.Ordinal.Equals(primary.DatabaseName, secondary.DatabaseName))
           || not (StringComparer.Ordinal.Equals(primary.SchemaSha256, secondary.SchemaSha256)) then
            invalidOp "Independent SQL Server JOIN attestation identities disagree."
        primary.AttachVerifiedJoinedProof(secondary)

    member private _.AttachVerifiedJoinedProof(secondary: SqlServerCteUniqueKeyAssurance) =
        SqlServerCteUniqueKeyAssurance(
            pinnedSnapshot, targetTable, sourceTable,
            SqlServerExactKeyTuple.ofAttested sourceKeyColumns sourceKeyKinds,
            sourceNullSemantics,
            (if targetKeyColumns.Length = 0 then None
             else Some(SqlServerExactKeyTuple.ofAttested targetKeyColumns targetKeyKinds)),
            databaseId, serverInstanceName, databaseName, databaseGuid,
            sourceObjectId, targetObjectId, uniqueIndexId, uniqueIndexName,
            metadataDigest, Some secondary)

    /// The caller MUST invoke this on the actual DML transaction/connection
    /// before executing the write. Locks are transaction-held; no detached
    /// preview-time result can satisfy this commit-time check.
    member this.IsCurrent(connection: DbConnection, transaction: DbTransaction) =
        if isNull connection || isNull transaction
           || not (Object.ReferenceEquals(transaction.Connection, connection)) then false
        else
            try
                // A joined proof spans three physical objects. Acquire all
                // transaction-held table locks in one canonical order
                // BEFORE either metadata witness reads the index catalog.
                // Without this, each independent two-table proof would
                // lock a different pair and invite inverted lock order.
                match joinedProof with
                | Some dependent ->
                    for tableName in [targetTable; sourceTable; dependent.SourceTable]
                                     |> List.distinct |> List.sort do
                        let schema, table =
                            SqlServerCteUniqueKeyAssurance.Parts "tableName" tableName
                        use lockCommand =
                            SqlServerCteUniqueKeyAssurance.Command connection transaction (
                                "SELECT TOP (1) 1 FROM [" + schema + "].[" + table
                                + "] WITH (TABLOCK, HOLDLOCK)")
                        lockCommand.ExecuteScalar() |> ignore
                | None -> ()
                let (dbId, liveServer, liveName, liveGuid, sourceId, targetId, indexId, indexName, digest, sourceKinds, targetKinds, nullSemantics) =
                    SqlServerCteUniqueKeyAssurance.Attest(
                        connection, transaction, pinnedSnapshot,
                        targetTable, sourceTable, sourceKeyColumns, targetKeyColumns, true)
                dbId = databaseId
                && StringComparer.Ordinal.Equals(liveServer, serverInstanceName)
                && StringComparer.Ordinal.Equals(liveName, databaseName)
                && liveGuid = databaseGuid
                && sourceId = sourceObjectId
                && targetId = targetObjectId && indexId = uniqueIndexId
                && StringComparer.Ordinal.Equals(indexName, uniqueIndexName)
                && StringComparer.Ordinal.Equals(digest, metadataDigest)
                && sourceKinds = sourceKeyKinds
                && targetKinds = targetKeyKinds
                && nullSemantics = sourceNullSemantics
                && (match joinedProof with
                    | Some dependent -> dependent.IsCurrent(connection, transaction)
                    | None -> true)
            with
            | :? ArgumentException
            | :? InvalidOperationException
            | :? DbException -> false

[<Sealed>]
type SqlPlanValidationContext private (
    policyVersion: string,
    allowedTables: IReadOnlySet<string> | null,
    dmlResultRowAssurance: DmlResultRowAssurance | null,
    schemaSnapshot: SqlSchemaSnapshot | null,
    sqliteUpdateFromKeyAssurance: SqliteUpdateFromKeyAssurance | null,
    sqlServerCteUniqueKeyAssurance: SqlServerCteUniqueKeyAssurance | null) =

    new(policyVersion: string) =
        SqlPlanValidationContext(policyVersion, null, null, null, null, null)

    new(policyVersion: string, allowedTables: IReadOnlySet<string> | null) =
        SqlPlanValidationContext(policyVersion, allowedTables, null, null, null, null)

    member _.PolicyVersion = policyVersion
    member _.AllowedTables = allowedTables
    member _.DmlResultRowAssurance = dmlResultRowAssurance
    member _.SchemaSnapshot = schemaSnapshot
    member _.SqliteUpdateFromKeyAssurance = sqliteUpdateFromKeyAssurance
    member _.SqlServerCteUniqueKeyAssurance = sqlServerCteUniqueKeyAssurance

    member _.WithDmlResultRowAssurance(assurance: DmlResultRowAssurance) =
        ArgumentNullException.ThrowIfNull(assurance)
        SqlPlanValidationContext(policyVersion, allowedTables, assurance, schemaSnapshot,
                                 sqliteUpdateFromKeyAssurance, sqlServerCteUniqueKeyAssurance)

    member _.WithSchemaSnapshot(snapshot: SqlSchemaSnapshot) =
        ArgumentNullException.ThrowIfNull(snapshot)
        SqlPlanValidationContext(policyVersion, allowedTables, dmlResultRowAssurance, snapshot,
                                 sqliteUpdateFromKeyAssurance, sqlServerCteUniqueKeyAssurance)

    member _.WithSqliteUpdateFromKeyAssurance(assurance: SqliteUpdateFromKeyAssurance) =
        ArgumentNullException.ThrowIfNull(assurance)
        if isNull schemaSnapshot then
            invalidOp "SQLite live PK assurance requires a schema snapshot first."
        SqlPlanValidationContext(policyVersion, allowedTables, dmlResultRowAssurance,
                                 schemaSnapshot, assurance, sqlServerCteUniqueKeyAssurance)

    member _.WithSqlServerCteUniqueKeyAssurance(assurance: SqlServerCteUniqueKeyAssurance) =
        ArgumentNullException.ThrowIfNull(assurance)
        match schemaSnapshot with
        | null -> ()
        | snapshot when snapshot.Provider <> SqlAgentToolType.MsSqlServer
                        || not (StringComparer.Ordinal.Equals(snapshot.Sha256, assurance.SchemaSha256)) ->
            invalidArg "assurance" "SQL Server CTE live proof must match the declared schema snapshot."
        | _ -> ()
        SqlPlanValidationContext(policyVersion, allowedTables, dmlResultRowAssurance,
                                 schemaSnapshot, sqliteUpdateFromKeyAssurance, assurance)

[<Sealed>]
type SqlExecutionPlanPolicy(queryMaxRows: int) =
    new() = SqlExecutionPlanPolicy(0)
    member _.QueryMaxRows = queryMaxRows

[<Sealed>]
type QueryExecutionResult(
    rows: IReadOnlyList<IReadOnlyDictionary<string, obj | null>>,
    rowCount: int,
    duration: TimeSpan,
    diagnostics: IReadOnlyList<string>) =
    member _.Rows = rows
    member _.RowCount = rowCount
    member _.Duration = duration
    member _.Diagnostics = diagnostics


type ISqlCommandExecutor =
    abstract ExecuteQueryAsync:
        CompiledSqlCommand *
        DbConnection *
        int *
        CancellationToken -> Task<QueryExecutionResult>
