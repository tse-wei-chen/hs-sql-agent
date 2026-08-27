using SqlKata.Compilers;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Maps the SqlKata compiler instance used during lowering back to the Core provider enum so
/// provider capability rules stay independent of SqlKata compiler types.
/// </summary>
internal static class SqlKataCompilerProviderClassifier
{
    internal static SqlAgentToolType Resolve(Compiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        return compiler switch
        {
            PostgresCompiler => SqlAgentToolType.Postgres,
            MySqlCompiler => SqlAgentToolType.MySQL,
            SqliteCompiler => SqlAgentToolType.Sqlite,
            SqlServerCompiler => SqlAgentToolType.MsSqlServer,
            OracleCompiler => SqlAgentToolType.Oracle,
            FirebirdCompiler => SqlAgentToolType.Firebird,
            _ => throw new SqlCompilationException(
                $"Unsupported SqlKata compiler type '{compiler.GetType().Name}'.")
        };
    }
}
