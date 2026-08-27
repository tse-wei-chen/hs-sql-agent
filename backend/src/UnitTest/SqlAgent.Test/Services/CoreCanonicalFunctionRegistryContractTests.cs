using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCanonicalFunctionRegistryContractTests
{
    [Theory]
    [InlineData("ABS", SqlCanonicalFunctionKind.Scalar, 1, 1, true)]
    [InlineData("ROUND", SqlCanonicalFunctionKind.Scalar, 1, 2, true)]
    [InlineData("SUM", SqlCanonicalFunctionKind.Aggregate, 1, 1, true)]
    [InlineData("LAG", SqlCanonicalFunctionKind.Window, 1, 3, true)]
    [InlineData("CORE_DATE_ADD", SqlCanonicalFunctionKind.Scalar, 3, 3, false)]
    [InlineData("CORE_STRING_AGG", SqlCanonicalFunctionKind.Aggregate, 2, 2, false)]
    public void Registry_DeclaresCanonicalRoleArityAndNormalizationSurface(
        string name,
        SqlCanonicalFunctionKind expectedKind,
        int expectedMinArguments,
        int expectedMaxArguments,
        bool expectedDirectPortable)
    {
        var contract = Assert.IsType<SqlCanonicalFunctionContract>(
            SqlCanonicalFunctionRegistry.Find(name));

        Assert.Equal(expectedKind, contract.Kind);
        Assert.Equal(expectedMinArguments, contract.MinArguments);
        Assert.Equal(expectedMaxArguments, contract.MaxArguments);
        Assert.Equal(expectedDirectPortable, contract.IsDirectPortable);
    }

    [Fact]
    public void Registry_DoesNotTreatRawStringAggregateAliasesAsCanonicalNames()
    {
        Assert.Null(SqlCanonicalFunctionRegistry.Find("STRING_AGG"));
        Assert.Null(SqlCanonicalFunctionRegistry.Find("GROUP_CONCAT"));
        Assert.Null(SqlCanonicalFunctionRegistry.Find("LISTAGG"));
        Assert.Null(SqlCanonicalFunctionRegistry.Find("LIST"));

        var canonical = Assert.IsType<SqlCanonicalFunctionContract>(
            SqlCanonicalFunctionRegistry.Find("CORE_STRING_AGG"));
        Assert.Equal(SqlCanonicalFunctionKind.Aggregate, canonical.Kind);
        Assert.False(canonical.AllowDistinct);
        Assert.True(canonical.AllowFilter);
        Assert.False(canonical.AllowWindow);
    }

    [Fact]
    public void Registry_WindowContracts_RequireOverAndRemainDirectPortable()
    {
        foreach (var contract in SqlCanonicalFunctionRegistry.All
                     .Where(item => item.Kind == SqlCanonicalFunctionKind.Window))
        {
            Assert.True(contract.RequireWindow);
            Assert.True(contract.AllowWindow);
            Assert.True(contract.IsDirectPortable);
        }
    }

    [Fact]
    public void Registry_DirectPortableContracts_AreNeverCoreInternalNames()
    {
        foreach (var contract in SqlCanonicalFunctionRegistry.All
                     .Where(item => item.IsDirectPortable))
        {
            Assert.False(
                contract.Name.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Compile_MySqlGroupConcatInWhere_StillUsesCanonicalAggregatePlacement()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT name FROM users WHERE GROUP_CONCAT(name) = 'x'",
                    SqlAgentToolType.MySQL),
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("canonical-function-registry-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(
            "Aggregate function 'CORE_STRING_AGG'",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
