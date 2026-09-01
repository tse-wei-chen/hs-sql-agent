namespace HsSqlAgent.SqlCore.Core.Execution

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open HsSqlAgent.SqlCore.Core.Compilation

[<AbstractClass; Sealed>]
type DmlFingerprintService private () =
    static member private Append(hash: IncrementalHash, value: string) =
        let bytes = Encoding.UTF8.GetBytes(value)
        let length = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(length.AsSpan(), bytes.Length)
        hash.AppendData(length)
        hash.AppendData(bytes)

    static member private CanonicalValue(value: obj | null) =
        match value with
        | null -> "null"
        | nonNullValue ->
            match nonNullValue with
            | :? string as text -> "string:" + text
            | :? bool as boolean -> if boolean then "bool:true" else "bool:false"
            | :? byte | :? sbyte | :? int16 | :? uint16 | :? int | :? uint32 | :? int64 | :? uint64 ->
                "integer:" + Convert.ToString(nonNullValue, CultureInfo.InvariantCulture)
            | :? single | :? double | :? decimal ->
                "number:" + Convert.ToString(nonNullValue, CultureInfo.InvariantCulture)
            | :? DateTime as dateTime ->
                "datetime:" + dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            | :? DateTimeOffset as offset ->
                "datetimeoffset:" + offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            | :? DateOnly as date -> "date:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            | :? TimeOnly as time -> "time:" + time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
            | :? Guid as guid -> "guid:" + guid.ToString("D")
            | :? (byte array) as bytes -> "bytes:" + Convert.ToHexString(bytes)
            | _ ->
                let valueType = nonNullValue.GetType()
                let typeName =
                    match valueType.FullName with
                    | null -> valueType.Name
                    | fullName -> fullName
                typeName + ":" + JsonSerializer.Serialize(nonNullValue, valueType)

    static member ComputePlanFingerprint(mutationCommand: CompiledSqlCommand, policyVersion: string) =
        if isNull mutationCommand then nullArg "mutationCommand"
        if String.IsNullOrWhiteSpace(policyVersion) then invalidArg "policyVersion" "Policy version cannot be empty."
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        DmlFingerprintService.Append(hash, mutationCommand.TargetProvider.ToString())
        DmlFingerprintService.Append(hash, mutationCommand.Kind.ToString())
        DmlFingerprintService.Append(hash, if mutationCommand.ReturnsRows then "returnsRows:true" else "returnsRows:false")
        DmlFingerprintService.Append(hash, mutationCommand.Sql)
        DmlFingerprintService.Append(hash, policyVersion)
        for parameter in mutationCommand.Parameters do
            DmlFingerprintService.Append(hash, parameter.Name)
            DmlFingerprintService.Append(hash, DmlFingerprintService.CanonicalValue(parameter.Value))
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()

    static member ComputeRowSetFingerprint(
        orderedKeys: IEnumerable<IReadOnlyList<obj | null> | null> | null) =
        match orderedKeys with
        | null -> nullArg "orderedKeys"
        | nonNullKeys ->
            use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            let mutable count = 0
            for key in nonNullKeys do
                match key with
                | null -> invalidArg "orderedKeys" "Row identity key cannot be null."
                | nonNullKey ->
                    DmlFingerprintService.Append(hash, "row")
                    for value in nonNullKey do
                        DmlFingerprintService.Append(hash, DmlFingerprintService.CanonicalValue(value))
                    count <- count + 1
            DmlFingerprintService.Append(hash, "count:" + count.ToString(CultureInfo.InvariantCulture))
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()

    static member private ComputeRowDigest(key: IReadOnlyList<obj | null> | null) =
        match key with
        | null -> invalidArg "key" "Row identity key cannot be null."
        | nonNullKey ->
            use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            DmlFingerprintService.Append(hash, "row")
            for value in nonNullKey do
                DmlFingerprintService.Append(hash, DmlFingerprintService.CanonicalValue(value))
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()

    static member ComputeUnorderedRowSetFingerprint(
        keys: IEnumerable<IReadOnlyList<obj | null> | null> | null) =
        match keys with
        | null -> nullArg "keys"
        | nonNullKeys ->
            let rows =
                nonNullKeys
                |> Seq.map DmlFingerprintService.ComputeRowDigest
                |> Seq.sort
                |> Seq.toArray
            use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            for row in rows do DmlFingerprintService.Append(hash,row)
            DmlFingerprintService.Append(hash,"count:" + rows.Length.ToString(CultureInfo.InvariantCulture))
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
