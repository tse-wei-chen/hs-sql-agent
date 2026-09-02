namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open System.Security.Cryptography
open System.Text
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

module internal CompileEvidenceBuilder =

    let schemaVersion = "2026-09-02.1"

    type Context =
        private
            { SourceProfile: SqlCompileProfileEvidence
              TargetProfile: SqlCompileProfileEvidence
              SourceCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>
              TargetCapabilities: ImmutableArray<SqlCompileCapabilityEvidence>
              Policy: SqlCompilePolicyEvidence
              Assurances: ImmutableArray<SqlCompileAssuranceEvidence> }

    let private ordinalIgnoreCaseCompare (left: string) (right: string) =
        let insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right)
        if insensitive <> 0 then insensitive else StringComparer.Ordinal.Compare(left, right)

    let private sortedStrings (values: seq<string>) =
        values
        |> Seq.sortWith ordinalIgnoreCaseCompare
        |> ImmutableArray.CreateRange

    let private settings (values: seq<string * string>) =
        values
        |> Seq.sortWith (fun (leftName, leftValue) (rightName, rightValue) ->
            let nameComparison = ordinalIgnoreCaseCompare leftName rightName
            if nameComparison <> 0 then nameComparison
            else StringComparer.Ordinal.Compare(leftValue, rightValue))
        |> Seq.map (fun (name, value) -> SqlCompileSettingEvidence(name, value))
        |> ImmutableArray.CreateRange

    let private profileEvidence provider (profile: SqlProviderCapabilityProfile | null) =
        let serverVersion, compatibilityLevel, sessionModes, sessionSettings =
            match profile with
            | null ->
                null,
                Nullable<int>(),
                ImmutableArray<string>.Empty,
                ImmutableArray<SqlCompileSettingEvidence>.Empty
            | value ->
                let version =
                    match value.ServerVersion with
                    | null -> null
                    | version -> version.ToString()
                let modes =
                    match value.SessionModes with
                    | null -> ImmutableArray<string>.Empty
                    | modes -> modes |> sortedStrings
                let profileSettings =
                    match value.SessionSettings with
                    | null -> ImmutableArray<SqlCompileSettingEvidence>.Empty
                    | items ->
                        items
                        |> Seq.map (fun pair -> pair.Key, pair.Value)
                        |> settings
                version, value.CompatibilityLevel, modes, profileSettings

        SqlCompileProfileEvidence(
            provider,
            serverVersion,
            compatibilityLevel,
            sessionModes,
            sessionSettings)

    let private capabilityStatus = function
        | SqlCapabilityStatus.Supported -> SqlCompileCapabilityStatus.Supported
        | SqlCapabilityStatus.Translated -> SqlCompileCapabilityStatus.Translated
        | SqlCapabilityStatus.Rejected -> SqlCompileCapabilityStatus.Rejected
        | value -> invalidOp ("Unknown capability status '" + string value + "'.")

    let private capabilityEvidence side provider (profile: SqlProviderCapabilityProfile | null) =
        SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities
        |> Seq.sortWith (fun left right ->
            let idComparison = ordinalIgnoreCaseCompare left.Id right.Id
            if idComparison <> 0 then idComparison
            else ordinalIgnoreCaseCompare left.Category right.Category)
        |> Seq.map (fun capability ->
            SqlCompileCapabilityEvidence(
                side,
                capability.Id,
                capability.Category,
                capabilityStatus capability.Status,
                capability.Detail))
        |> ImmutableArray.CreateRange

    let private joinColumns (columns: ImmutableArray<string>) =
        if columns.IsDefaultOrEmpty then String.Empty
        else String.Join("|", columns)

    let private conflictAssuranceEvidence (assurance: DmlConflictTargetAssurance) =
        let details =
            [
                "primaryKeyColumns", joinColumns assurance.PrimaryKeyColumns
                "matchedUniqueKeyColumns", joinColumns assurance.MatchedUniqueKeyColumns
                "matchedUniqueKeyName", (if isNull assurance.MatchedUniqueKeyName then String.Empty else assurance.MatchedUniqueKeyName)
                "matchedUniqueKeyIsPrimaryKey", assurance.MatchedUniqueKeyIsPrimaryKey.ToString(CultureInfo.InvariantCulture)
                "enforcedUniqueKeyCount", assurance.EnforcedUniqueKeyCount.ToString(CultureInfo.InvariantCulture)
                "hasUnsupportedEnforcedUniqueKeys", assurance.HasUnsupportedEnforcedUniqueKeys.ToString(CultureInfo.InvariantCulture)
                "isSoleEnforcedUniqueKey", assurance.IsSoleEnforcedUniqueKey.ToString(CultureInfo.InvariantCulture)
                "sourceRowsUniqueByInsertColumns", joinColumns assurance.SourceRowsUniqueByInsertColumns
            ]
            |> settings
        SqlCompileAssuranceEvidence("dml.conflict_target", details)

    let private resultRowAssuranceEvidence (assurance: DmlResultRowAssurance) =
        let details =
            [
                "targetTable", assurance.TargetTable
                "operation", assurance.Operation.ToString()
            ]
            |> settings
        SqlCompileAssuranceEvidence("dml.result_rows", details)

    let private assuranceEvidence
        (conflictTargetAssurance: DmlConflictTargetAssurance | null)
        (resultRowAssurance: DmlResultRowAssurance | null) =

        [
            match conflictTargetAssurance with
            | null -> ()
            | value -> yield conflictAssuranceEvidence value
            match resultRowAssurance with
            | null -> ()
            | value -> yield resultRowAssuranceEvidence value
        ]
        |> Seq.sortBy (fun evidence -> evidence.Kind)
        |> ImmutableArray.CreateRange

    let create
        sourceProvider
        targetProvider
        (sourceProfile: SqlProviderCapabilityProfile | null)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (conflictTargetAssurance: DmlConflictTargetAssurance | null)
        (resultRowAssurance: DmlResultRowAssurance | null)
        policyVersion
        queryMaxRows
        requireUpdatePredicate
        requireDeletePredicate
        (allowedTables: string list option) =

        let allowed =
            match allowedTables with
            | None -> ImmutableArray<string>.Empty
            | Some values -> values |> sortedStrings

        { SourceProfile = profileEvidence sourceProvider sourceProfile
          TargetProfile = profileEvidence targetProvider targetProfile
          SourceCapabilities =
            capabilityEvidence SqlCompileCapabilitySide.Source sourceProvider sourceProfile
          TargetCapabilities =
            capabilityEvidence SqlCompileCapabilitySide.Target targetProvider targetProfile
          Policy =
            SqlCompilePolicyEvidence(
                policyVersion,
                queryMaxRows,
                requireUpdatePredicate,
                requireDeletePredicate,
                allowed)
          Assurances =
            assuranceEvidence conflictTargetAssurance resultRowAssurance }

    let private appendToken (builder: StringBuilder) (value: string | null) =
        if isNull value then
            builder.Append("-1:;") |> ignore
        else
            builder.Append(value.Length).Append(':').Append(value).Append(';') |> ignore

    let private appendInt (builder: StringBuilder) value =
        appendToken builder (value.ToString(CultureInfo.InvariantCulture))

    let private appendBool (builder: StringBuilder) value =
        appendToken builder (if value then "1" else "0")

    let private appendProfile (builder: StringBuilder) (profile: SqlCompileProfileEvidence) =
        appendInt builder (int profile.Provider)
        appendToken builder profile.ServerVersion
        appendToken builder (
            if profile.CompatibilityLevel.HasValue then
                profile.CompatibilityLevel.Value.ToString(CultureInfo.InvariantCulture)
            else null)
        appendInt builder profile.SessionModes.Length
        profile.SessionModes |> Seq.iter (appendToken builder)
        appendInt builder profile.SessionSettings.Length
        profile.SessionSettings
        |> Seq.iter (fun item ->
            appendToken builder item.Name
            appendToken builder item.Value)

    let private appendCapabilities (builder: StringBuilder) (capabilities: ImmutableArray<SqlCompileCapabilityEvidence>) =
        appendInt builder capabilities.Length
        capabilities
        |> Seq.iter (fun capability ->
            appendInt builder (int capability.Side)
            appendToken builder capability.Id
            appendToken builder capability.Category
            appendInt builder (int capability.Status)
            appendToken builder capability.Detail)

    let private appendPolicy (builder: StringBuilder) (policy: SqlCompilePolicyEvidence) =
        appendToken builder policy.PolicyVersion
        appendInt builder policy.QueryMaxRows
        appendBool builder policy.RequireUpdatePredicate
        appendBool builder policy.RequireDeletePredicate
        appendInt builder policy.AllowedTables.Length
        policy.AllowedTables |> Seq.iter (appendToken builder)

    let private appendAssurances (builder: StringBuilder) (assurances: ImmutableArray<SqlCompileAssuranceEvidence>) =
        appendInt builder assurances.Length
        assurances
        |> Seq.iter (fun assurance ->
            appendToken builder assurance.Kind
            appendInt builder assurance.Details.Length
            assurance.Details
            |> Seq.iter (fun detail ->
                appendToken builder detail.Name
                appendToken builder detail.Value))

    let private fingerprint context verdict decisionBoundary =
        let builder = StringBuilder()
        appendToken builder schemaVersion
        appendToken builder SqlCapabilityMatrix.Version
        appendProfile builder context.SourceProfile
        appendProfile builder context.TargetProfile
        appendCapabilities builder context.SourceCapabilities
        appendCapabilities builder context.TargetCapabilities
        appendPolicy builder context.Policy
        appendAssurances builder context.Assurances
        appendInt builder (int verdict)
        appendInt builder (int decisionBoundary)
        SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let build context verdict decisionBoundary (planFingerprint: string | null) =
        let normalizedPlanFingerprint =
            if String.IsNullOrWhiteSpace(planFingerprint) then null else planFingerprint
        SqlCompileEvidence(
            schemaVersion,
            SqlCapabilityMatrix.Version,
            context.SourceProfile,
            context.TargetProfile,
            context.SourceCapabilities,
            context.TargetCapabilities,
            context.Policy,
            context.Assurances,
            verdict,
            decisionBoundary,
            normalizedPlanFingerprint,
            fingerprint context verdict decisionBoundary)
