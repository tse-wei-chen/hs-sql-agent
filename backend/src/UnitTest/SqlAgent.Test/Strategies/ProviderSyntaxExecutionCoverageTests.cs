using Xunit;

namespace SqlAgent.Test.Strategies;

public sealed class ProviderSyntaxExecutionCoverageTests
{
    private static readonly string[] CommonCteExecutionMethods =
    [
        nameof(BaseStrategyTests<ISqlStrategy, IDbFixture>.ExecuteRawQueryAsync_CteWhereOrder_ShouldCompileRenderAndExecute),
        nameof(BaseStrategyTests<ISqlStrategy, IDbFixture>.ExecuteRawQueryAsync_CteUnionAllBody_ShouldCompileRenderAndExecute),
        nameof(BaseStrategyTests<ISqlStrategy, IDbFixture>.ExecuteRawQueryAsync_CteJoin_ShouldCompileRenderAndExecute),
        nameof(BaseStrategyTests<ISqlStrategy, IDbFixture>.ExecuteRawQueryAsync_CteGroupHaving_ShouldCompileRenderAndExecute),
        nameof(BaseStrategyTests<ISqlStrategy, IDbFixture>.ExecuteRawQueryAsync_CteCorrelatedExists_ShouldCompileRenderAndExecute),
        nameof(BaseStrategyTests<ISqlStrategy, IDbFixture>.ExecuteRawQueryAsync_CteWindow_ShouldCompileRenderAndExecute),
        nameof(BaseStrategyTests<ISqlStrategy, IDbFixture>.ExecuteRawQueryAsync_ChainedCtes_ShouldCompileRenderAndExecute)
    ];

    private static readonly Type[] ProviderTestTypes =
    [
        typeof(PostgresStrategyTests),
        typeof(MySqlStrategyTests),
        typeof(MsSqlServerStrategyTests),
        typeof(SqliteStrategyTests),
        typeof(OracleStrategyTests),
        typeof(FirebirdStrategyTests)
    ];

    [Fact]
    public void RealProviderCteExecution_HasStableFiftyCaseFloor()
    {
        Assert.Equal(7, CommonCteExecutionMethods.Length);
        Assert.Equal(6, ProviderTestTypes.Length);

        foreach (var providerType in ProviderTestTypes)
        {
            foreach (var methodName in CommonCteExecutionMethods)
            {
                var method = providerType.GetMethod(methodName);
                Assert.NotNull(method);
                Assert.NotNull(
                    method.GetCustomAttributes(typeof(FactAttribute), inherit: true)
                        .SingleOrDefault());
            }

            var native = providerType.GetMethod(
                providerType == typeof(PostgresStrategyTests)
                    ? "ExecuteRawQueryAsync_CtePostgresNativeSyntax_Executes"
                    : providerType == typeof(MySqlStrategyTests)
                        ? "ExecuteRawQueryAsync_CteMySqlNativeSyntax_Executes"
                        : providerType == typeof(MsSqlServerStrategyTests)
                            ? "ExecuteRawQueryAsync_CteSqlServerNativeSyntax_Executes"
                            : providerType == typeof(SqliteStrategyTests)
                                ? "ExecuteRawQueryAsync_CteSqliteNativeSyntax_Executes"
                                : providerType == typeof(OracleStrategyTests)
                                    ? "ExecuteRawQueryAsync_CteOracleNativeSyntax_Executes"
                                    : "ExecuteRawQueryAsync_CteFirebirdNativeSyntax_Executes");
            Assert.NotNull(native);
            Assert.NotNull(
                native.GetCustomAttributes(typeof(FactAttribute), inherit: true)
                    .SingleOrDefault());
        }

        Assert.NotNull(
            typeof(PostgresStrategyTests).GetMethod(
                "ExecuteRawQueryAsync_NestedInnerCte_Executes"));
        Assert.NotNull(
            typeof(MySqlStrategyTests).GetMethod(
                "ExecuteRawQueryAsync_NestedInnerCte_Executes"));

        const int inheritedCommonCases = 7 * 6;
        const int dialectNativeCases = 6;
        const int nestedInnerCteCases = 2;

        Assert.Equal(
            50,
            inheritedCommonCases +
            dialectNativeCases +
            nestedInnerCteCases);
    }
}
