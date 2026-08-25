using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

internal static class CoreLikeEscapeSqlRenderer
{
    public static string RenderSuffix(BinaryExpr binary, Compiler compiler)
    {
        if (binary.LikeEscape is null)
            return string.Empty;

        if (binary.Operator is not ("LIKE" or "ILIKE"))
        {
            throw new SqlCompilationException(
                $"LIKE ESCAPE metadata is valid only with LIKE/ILIKE, not '{binary.Operator}'.");
        }

        var escape = binary.LikeEscape;
        if (escape.Length != 1 || char.IsControl(escape[0]))
        {
            throw new SqlCompilationException(
                "LIKE ESCAPE requires exactly one non-control character.");
        }

        var literal = escape[0] == '\\'
            ? compiler switch
            {
                PostgresCompiler => "E'\\\\'",
                MySqlCompiler => "CHAR(92)",
                _ => "'\\'"
            }
            : "'" + escape.Replace("'", "''", StringComparison.Ordinal) + "'";

        return $" ESCAPE {literal}";
    }
}
