using System.Globalization;

namespace HsSqlAgent.SqlCore.Models;

internal readonly record struct SqlDecimalShape(int Precision, int Scale);

/// <summary>
/// Exact-decimal target contract for Firebird. Firebird 3.x accepts precision up to 18, while
/// Firebird 4.0 introduced INT128-backed NUMERIC/DECIMAL precision up to 38. The Core value model
/// uses System.Decimal, so its actual value shape is derived without widening or truncating scale.
/// </summary>
internal static class SqlFirebirdDecimalCapabilityRules
{
    internal const int LegacyMaximumPrecision = 18;
    internal static readonly Version ExtendedPrecisionMinimumVersion = new(4, 0);

    internal static bool RequiresTargetProfileValidation(
        SqlAgentToolType provider) =>
        provider == SqlAgentToolType.Firebird;

    internal static SqlDecimalShape Shape(decimal value)
    {
        var text = Math.Abs(value).ToString(
            "0.############################",
            CultureInfo.InvariantCulture);
        var separator = text.IndexOf('.');
        var integerPart = separator < 0 ? text : text[..separator];
        var fractionalPart = separator < 0 ? string.Empty : text[(separator + 1)..];

        var integerDigits = integerPart.TrimStart('0').Length;
        var scale = fractionalPart.Length;
        var precision = Math.Max(1, integerDigits + scale);
        return new SqlDecimalShape(precision, scale);
    }

    internal static string FirebirdCastType(decimal value)
    {
        var shape = Shape(value);
        return $"DECIMAL({shape.Precision},{shape.Scale})";
    }

    internal static string? TargetValidationError(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile,
        decimal value)
    {
        if (provider != SqlAgentToolType.Firebird)
            return null;

        var shape = Shape(value);
        if (shape.Precision <= LegacyMaximumPrecision)
            return null;

        if (targetProfile is
            {
                Provider: SqlAgentToolType.Firebird,
                ServerVersion: { } version
            }
            && version.CompareTo(ExtendedPrecisionMinimumVersion) >= 0)
        {
            return null;
        }

        return
            "SQL capability 'numeric.decimal_extended' requires an explicit Firebird target " +
            "capability profile with ServerVersion 4.0 or newer for exact decimal precision " +
            $"above {LegacyMaximumPrecision}; this value requires DECIMAL({shape.Precision},{shape.Scale}).";
    }

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (provider != SqlAgentToolType.Firebird)
        {
            return new SqlCapability(
                "numeric.decimal_extended",
                "numeric",
                SqlCapabilityStatus.Translated,
                "Extended System.Decimal values use the target provider's existing exact-numeric parameter contract; the Firebird-specific 18-digit legacy limit does not apply.");
        }

        var supported = targetProfile is
        {
            Provider: SqlAgentToolType.Firebird,
            ServerVersion: { } version
        } && version.CompareTo(ExtendedPrecisionMinimumVersion) >= 0;

        return new SqlCapability(
            "numeric.decimal_extended",
            "numeric",
            supported ? SqlCapabilityStatus.Translated : SqlCapabilityStatus.Rejected,
            supported
                ? "Firebird 4.0+ target profiles support exact DECIMAL precision above 18 through INT128-backed DECIMAL(p,s); Core derives p/s from the actual decimal value."
                : "Exact Firebird decimal values requiring precision above 18 remain fail-closed unless the target capability profile explicitly declares ServerVersion 4.0 or newer.");
    }
}
