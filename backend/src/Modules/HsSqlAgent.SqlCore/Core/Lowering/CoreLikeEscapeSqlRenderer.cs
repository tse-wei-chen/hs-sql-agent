namespace HsSqlAgent.SqlCore.Core.Lowering;

internal static class CoreLikeEscapeSqlRenderer
{
    public static string RenderSuffix(
        BinaryExpr binary,
        SqlAgentToolType provider)
    {
        if (binary.LikeEscape is null)
            return string.Empty;

        if (binary.Operator is not ("LIKE" or "ILIKE"))
        {
            throw new SqlCompilationException(
                "LIKE ESCAPE metadata is valid only with LIKE/ILIKE, not '" +
                binary.Operator + "'.");
        }

        var escape = binary.LikeEscape;
        if (escape.Length != 1 || char.IsControl(escape[0]))
        {
            throw new SqlCompilationException(
                "LIKE ESCAPE requires exactly one non-control character.");
        }

        string literal;
        if (escape[0] == '\\')
        {
            literal = provider switch
            {
                SqlAgentToolType.Postgres => "E'\\\\'",
                SqlAgentToolType.MySQL => "CHAR(92)",
                _ => "'\\'"
            };
        }
        else
        {
            literal = "'" + escape.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        return " ESCAPE " + literal;
    }
}
