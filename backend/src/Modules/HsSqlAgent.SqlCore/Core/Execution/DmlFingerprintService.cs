using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.SqlCore.Core.Compilation;

namespace HsSqlAgent.SqlCore.Core.Execution;

public static class DmlFingerprintService
{
    public static string ComputePlanFingerprint(
        CompiledSqlCommand mutationCommand,
        string policyVersion)
    {
        ArgumentNullException.ThrowIfNull(mutationCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, mutationCommand.TargetProvider.ToString());
        Append(hash, mutationCommand.Kind.ToString());
        Append(hash, mutationCommand.ReturnsRows ? "returnsRows:true" : "returnsRows:false");
        Append(hash, mutationCommand.Sql);
        Append(hash, policyVersion);

        foreach (var parameter in mutationCommand.Parameters)
        {
            Append(hash, parameter.Name);
            Append(hash, CanonicalValue(parameter.Value));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeRowSetFingerprint(
        IEnumerable<IReadOnlyList<object?>> orderedKeys)
    {
        ArgumentNullException.ThrowIfNull(orderedKeys);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        foreach (var key in orderedKeys)
        {
            Append(hash, "row");
            foreach (var value in key)
                Append(hash, CanonicalValue(value));
            count++;
        }
        Append(hash, $"count:{count.ToString(CultureInfo.InvariantCulture)}");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeUnorderedRowSetFingerprint(
        IEnumerable<IReadOnlyList<object?>> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rowDigests = keys.Select(ComputeRowDigest)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var rowDigest in rowDigests)
            Append(hash, rowDigest);
        Append(hash, $"count:{rowDigests.Length.ToString(CultureInfo.InvariantCulture)}");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeRowDigest(IReadOnlyList<object?> key)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "row");
        foreach (var value in key)
            Append(hash, CanonicalValue(value));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string CanonicalValue(object? value)
    {
        if (value is null) return "null";
        return value switch
        {
            string text => "string:" + text,
            bool boolean => boolean ? "bool:true" : "bool:false",
            byte or sbyte or short or ushort or int or uint or long or ulong
                => "integer:" + Convert.ToString(value, CultureInfo.InvariantCulture),
            float or double or decimal
                => "number:" + Convert.ToString(value, CultureInfo.InvariantCulture),
            DateTime dateTime => "datetime:" + dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset offset => "datetimeoffset:" + offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => "date:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => "time:" + time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            Guid guid => "guid:" + guid.ToString("D"),
            byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
            _ => value.GetType().FullName + ":" + JsonSerializer.Serialize(value, value.GetType())
        };
    }
}
