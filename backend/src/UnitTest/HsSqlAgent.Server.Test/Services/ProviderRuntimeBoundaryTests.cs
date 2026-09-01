using System.Data.Common;
using System.Reflection;
using HsSqlAgent.Server.Services;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class ProviderRuntimeBoundaryTests
{
    [Fact]
    public void TypedQueryRuntime_PublicContract_IsProviderNative()
    {
        var execute = Assert.Single(typeof(ITypedQueryRuntime).GetMethods());
        Assert.Equal(typeof(ISqlProvider), execute.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(
            typeof(TypedQueryRuntime).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters()),
            parameter => typeof(ISqlStrategy).IsAssignableFrom(parameter.ParameterType));
    }

    [Fact]
    public void QueryExecutorContract_RequiresCallerOwnedOpenConnection()
    {
        var execute = Assert.Single(typeof(ISqlCommandExecutor).GetMethods());
        var parameters = execute.GetParameters();

        Assert.Contains(parameters, parameter =>
            parameter.ParameterType == typeof(DbConnection));
        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType == typeof(string)
            && parameter.Name?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true);

        var executorMethods = typeof(CompiledSqlCommandExecutor)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == nameof(CompiledSqlCommandExecutor.ExecuteQueryAsync))
            .ToArray();
        Assert.Single(executorMethods);
        Assert.Contains(executorMethods[0].GetParameters(), parameter =>
            parameter.ParameterType == typeof(DbConnection));
        Assert.Empty(typeof(CompiledSqlCommandExecutor).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(IDbConnectionFactory)));
    }

    [Fact]
    public void TypedDmlRuntime_PublicContract_IsProviderNative()
    {
        var methods = typeof(TypedDmlRuntime)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.Name is nameof(TypedDmlRuntime.PreviewAsync) or nameof(TypedDmlRuntime.CommitAsync))
            .ToArray();

        Assert.Equal(2, methods.Length);
        Assert.All(methods, method => Assert.Equal(typeof(ISqlProvider), method.GetParameters()[0].ParameterType));
        Assert.DoesNotContain(
            methods.SelectMany(method => method.GetParameters()),
            parameter => typeof(ISqlStrategy).IsAssignableFrom(parameter.ParameterType));
    }

    [Fact]
    public void DmlCoordinatorContract_RequiresVerifiedRuntimeVersionProof()
    {
        var contractMethods = typeof(IDmlCoordinator).GetMethods();
        Assert.Equal(2, contractMethods.Length);
        Assert.All(contractMethods, method => Assert.Contains(
            method.GetParameters(),
            parameter => parameter.Name == "expectedServerVersionIdentity"
                         && parameter.ParameterType == typeof(string)));

        var legacyOverloads = typeof(DmlCoordinator)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.Name is nameof(DmlCoordinator.PreviewAsync) or nameof(DmlCoordinator.CommitAsync))
            .Where(method => method.GetParameters().All(parameter => parameter.Name != "expectedServerVersionIdentity"))
            .ToArray();

        Assert.Equal(2, legacyOverloads.Length);
        Assert.All(legacyOverloads, method =>
        {
            var obsolete = Assert.Single(method.GetCustomAttributes<ObsoleteAttribute>());
            Assert.True(obsolete.IsError);
        });
    }

    [Fact]
    public void ProviderStrategyBase_ImplementsRuntimeCapabilitiesDirectly()
    {
        Assert.True(typeof(ISqlProvider).IsAssignableFrom(typeof(BaseSqlStrategy)));
        Assert.True(typeof(IDbConnectionFactory).IsAssignableFrom(typeof(BaseSqlStrategy)));
        Assert.True(typeof(IProviderMetadataReader).IsAssignableFrom(typeof(BaseSqlStrategy)));
    }

    [Fact]
    public void StrategyRuntimeCompatibilityBridges_HaveBeenRemoved()
    {
        var assembly = typeof(TypedQueryRuntime).Assembly;
        Assert.Null(assembly.GetType(
            "HsSqlAgent.Server.Services.TypedQueryRuntimeStrategyCompatibilityExtensions",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "HsSqlAgent.Server.Services.TypedDmlRuntimeStrategyCompatibilityExtensions",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "HsSqlAgent.Server.Tools.TypedDmlApprovalFlowStrategyCompatibilityExtensions",
            throwOnError: false));
    }

    [Fact]
    public void TransitionalStrategyFactoryAlias_HasBeenRemoved()
    {
        var serviceAssembly = typeof(ISqlProviderFactory).Assembly;
        Assert.Null(serviceAssembly.GetType(
            "SqlAgent.Service.Factories.ISqlStrategyFactory",
            throwOnError: false));
    }

    [Fact]
    public void StrategyBackedProviderAdapter_HasBeenRemoved()
    {
        var serviceAssembly = typeof(ISqlProvider).Assembly;
        Assert.Null(serviceAssembly.GetType(
            "SqlAgent.Service.Strategies.Adapters.StrategyBackedSqlProviderFactory",
            throwOnError: false));
        Assert.Null(serviceAssembly.GetType(
            "SqlAgent.Service.Strategies.Adapters.LegacySqlProviderAdapter",
            throwOnError: false));
    }
}