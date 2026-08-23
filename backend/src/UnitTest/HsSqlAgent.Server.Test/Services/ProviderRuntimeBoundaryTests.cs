using System.Reflection;
using HsSqlAgent.Server.Services;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Strategies;
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
    public void LegacySqlProviderAdapter_TypeHasBeenRemoved()
    {
        var serviceAssembly = typeof(ISqlStrategy).Assembly;
        Assert.Null(serviceAssembly.GetType(
            "SqlAgent.Service.Strategies.Adapters.LegacySqlProviderAdapter",
            throwOnError: false));
    }
}
