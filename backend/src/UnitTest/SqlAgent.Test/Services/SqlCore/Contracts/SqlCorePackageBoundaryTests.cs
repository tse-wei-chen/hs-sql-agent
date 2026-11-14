using HsSqlAgent.SqlCore;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCorePackageBoundaryTests
{
    [Fact]
    public void CompilerSurface_IsOwnedByFSharpSqlCoreAssembly()
    {
        var coreAssembly = typeof(SqlCoreFacade).Assembly;
        var contractsAssembly = typeof(CoreSqlTextParser).Assembly;

        Assert.Equal("HsSqlAgent.SqlCore", coreAssembly.GetName().Name);
        Assert.Same(coreAssembly, typeof(CoreSqlCompiler).Assembly);
        Assert.Same(coreAssembly, typeof(CoreDmlCompiler).Assembly);

        Assert.Same(coreAssembly, contractsAssembly);
        Assert.Same(coreAssembly, typeof(SqlCapabilityMatrix).Assembly);
        Assert.Same(coreAssembly, typeof(SqlTemporalLiteralParser).Assembly);

        Assert.NotSame(coreAssembly, typeof(MySqlProvider).Assembly);
        Assert.Equal("HsSqlAgent.Provider.MySql", typeof(MySqlProvider).Assembly.GetName().Name);
    }

    [Fact]
    public void SqlCoreAssemblies_DoNotReferenceRuntimeDatabaseDriversOrDapper()
    {
        var forbidden = new[]
        {
            "Dapper",
            "SqlKata",
            "MySql.Data",
            "Npgsql",
            "Microsoft.Data.Sqlite",
            "Microsoft.Data.SqlClient",
            "Oracle.ManagedDataAccess",
            "Oracle.ManagedDataAccess.Core",
            "FirebirdSql.Data.FirebirdClient"
        };

        foreach (var assembly in new[] { typeof(SqlCoreFacade).Assembly, typeof(CoreSqlTextParser).Assembly })
        {
            var references = assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var dependency in forbidden)
                Assert.DoesNotContain(dependency, references);
        }
    }
}
