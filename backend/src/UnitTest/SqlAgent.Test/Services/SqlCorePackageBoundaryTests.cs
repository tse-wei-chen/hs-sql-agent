using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCorePackageBoundaryTests
{
    [Fact]
    public void CompilerSurface_IsOwnedBySqlCoreAssembly()
    {
        var coreAssembly = typeof(CoreSqlCompiler).Assembly;

        Assert.Equal("HsSqlAgent.SqlCore", coreAssembly.GetName().Name);
        Assert.Same(coreAssembly, typeof(CoreDmlCompiler).Assembly);
        Assert.Same(coreAssembly, typeof(CoreSqlTextParser).Assembly);
        Assert.Same(coreAssembly, typeof(SqlCapabilityMatrix).Assembly);
        Assert.NotSame(coreAssembly, typeof(MySqlProvider).Assembly);
        Assert.Equal("SqlAgent.Service", typeof(MySqlProvider).Assembly.GetName().Name);
    }

    [Fact]
    public void SqlCoreAssembly_DoesNotReferenceRuntimeDatabaseDriversOrDapper()
    {
        var references = typeof(CoreSqlCompiler).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var forbidden = new[]
        {
            "Dapper",
            "MySql.Data",
            "Npgsql",
            "Microsoft.Data.Sqlite",
            "Microsoft.Data.SqlClient",
            "Oracle.ManagedDataAccess",
            "Oracle.ManagedDataAccess.Core",
            "FirebirdSql.Data.FirebirdClient"
        };

        foreach (var dependency in forbidden)
            Assert.DoesNotContain(dependency, references);
    }
}
