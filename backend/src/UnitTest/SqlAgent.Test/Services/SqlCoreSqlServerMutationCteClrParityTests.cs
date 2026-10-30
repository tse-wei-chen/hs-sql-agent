using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server CTE-prefixed mutations are public CLR statements, not just
/// internal F# AST nodes. No constructor, conversion, or target rewrite may
/// silently erase read-only CTE definitions before authorization.
/// </summary>
public sealed class SqlCoreSqlServerMutationCteClrParityTests
{
    private static SqlProviderCapabilityProfile Profile() =>
        new(SqlAgentToolType.MsSqlServer, new Version(16, 0));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void PublicClrMutationPreservesCtesAndMatchesNativeTypedCompilation(bool update, bool chain)
    {
        var with = chain
            ? "WITH seed AS (SELECT 7 AS id), picked(id) AS (SELECT id FROM seed) "
            : "WITH picked AS (SELECT 7 AS id) ";
        var sql = with + (update
            ? "UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id"
            : "DELETE FROM users FROM picked WHERE users.id = picked.id");

        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, Profile());
        var clrCtes = parsed.Statement switch
        {
            UpdateStatement item => item.Ctes,
            DeleteStatement item => item.Ctes,
            _ => throw new Xunit.Sdk.XunitException("CTE mutation did not return a public CLR DML AST.")
        };
        Assert.Equal(chain ? 2 : 1, clrCtes.Length);
        Assert.Equal("picked", clrCtes[^1].Name.Parts[^1].Value);
        Assert.NotNull(clrCtes[^1].Query);
        Assert.All(clrCtes, cte => Assert.False(cte.RecursiveScope));

        var validation = new SqlPlanValidationContext("sqlserver-clr-cte-parity",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });
        var direct = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            validation, new DmlCompilationPolicy(), Profile(), Profile(),
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
        var converted = SqlCoreFacade.CompileDml(parsed,
            SqlAgentToolType.MsSqlServer, validation, new DmlCompilationPolicy(),
            Profile(), conflictTargetAssurance: null);
        Assert.Equal(update ? SqlStatementKind.Update : SqlStatementKind.Delete, converted.Kind);
        Assert.Equal(direct.Sql, converted.Sql);
        Assert.Equal(direct.Parameters.Select(p => p.Value),
            converted.Parameters.Select(p => p.Value));
        Assert.StartsWith("WITH ", converted.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicClrCteWithPhysicalScanDoesNotInheritSingletonAuthorization(bool update)
    {
        var sql = "WITH picked AS (SELECT id FROM private_source) " +
            (update
                ? "UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id"
                : "DELETE FROM users FROM picked WHERE users.id = picked.id");
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, Profile());
        var validation = new SqlPlanValidationContext("sqlserver-clr-cte-physical-negative",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users", "private_source" });
        Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(parsed, SqlAgentToolType.MsSqlServer,
                validation, new DmlCompilationPolicy(), Profile(),
                conflictTargetAssurance: null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicClrCteRequiresSqlServer2005OrLater(bool update)
    {
        var sql = "WITH picked AS (SELECT 7 AS id) " +
            (update
                ? "UPDATE users SET label = 1 FROM picked WHERE users.id = picked.id"
                : "DELETE FROM users FROM picked WHERE users.id = picked.id");
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(8, 0));
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, old));
    }
}
