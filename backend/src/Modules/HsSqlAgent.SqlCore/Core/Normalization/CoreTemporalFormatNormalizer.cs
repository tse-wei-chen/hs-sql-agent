namespace HsSqlAgent.SqlCore.Core.Normalization;

/// <summary>
/// Focused migration seam for temporal format-token translation.
/// Canonical function control-flow, validation, AST construction, and diagnostics are owned by F#.
/// Delete this seam when DateFormatTranslator itself moves to F#.
/// </summary>
internal static class CoreTemporalFormatNormalizer
{
    private static readonly DateFormatTranslator DateFormats = new();

    internal static string Translate(
        string sourceFormat,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        DateFormats.Translate(sourceFormat, sourceDialect, targetProvider);
}
