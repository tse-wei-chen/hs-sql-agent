using System.Text.RegularExpressions;

namespace Admin.Service.Services;

/// <summary>
/// Shared conservative masking for audit detail and operator-supplied evidence.
/// Redaction is applied before persistence and before durable SIEM outbox creation.
/// </summary>
public static class AuditTextRedactor
{
    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = Regex.Replace(
            value,
            @"(?i)\b(password|pwd|token|api[_-]?key|secret)\s*[=:]\s*[^;\s]+",
            "$1=[REDACTED]",
            RegexOptions.CultureInvariant);
        return Regex.Replace(
            redacted,
            @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
            "Bearer [REDACTED]",
            RegexOptions.CultureInvariant);
    }
}
