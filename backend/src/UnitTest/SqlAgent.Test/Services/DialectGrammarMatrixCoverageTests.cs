using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DialectGrammarMatrixCoverageTests
{
    [Fact]
    public void PositiveGrammarMatrices_HaveStableCrossDialectFloor()
    {
        var postgres = PostgresGrammarMatrixTests.PostgresCteGrammarMatrix().Count();
        var mySql = MySqlGrammarMatrixTests.MySqlCteGrammarMatrix().Count();
        var sqlServer = SqlServerGrammarMatrixTests.SqlServerCteGrammarMatrix().Count();
        var sqlite = SqliteGrammarMatrixTests.SqliteCteGrammarMatrix().Count();
        var oracle = OracleGrammarMatrixTests.OracleCteGrammarMatrix().Count();
        var firebird = FirebirdGrammarMatrixTests.FirebirdCteGrammarMatrix().Count();

        Assert.Equal(432, postgres);
        Assert.Equal(900, mySql);
        Assert.Equal(528, sqlServer);
        Assert.Equal(825, sqlite);
        Assert.Equal(900, oracle);
        Assert.Equal(900, firebird);
        Assert.Equal(
            4485,
            postgres + mySql + sqlServer + sqlite + oracle + firebird);
    }
}
