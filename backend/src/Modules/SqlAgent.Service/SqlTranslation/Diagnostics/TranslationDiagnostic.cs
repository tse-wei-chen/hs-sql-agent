namespace SqlAgent.Service.SqlTranslation.Diagnostics;

public enum UnknownFunctionPolicy
{
    Passthrough,
    WarnAndPassthrough,
    Throw
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum FunctionPortability
{
    Native,
    Equivalent,
    Emulated,
    Unsupported,
    Unknown
}

public sealed record TranslationDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    FunctionPortability? Portability = null);

public sealed record SqlTranslationResult(
    string Sql,
    IReadOnlyList<TranslationDiagnostic> Diagnostics);
