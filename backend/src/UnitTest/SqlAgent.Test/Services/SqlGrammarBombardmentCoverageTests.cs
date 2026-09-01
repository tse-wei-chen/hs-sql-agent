using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlGrammarBombardmentCoverageTests
{
    [Fact]
    public void GeneratedGrammarBombardment_HasStableCrossDialectFloor()
    {
        var positiveQuery =
            PostgresGrammarMatrixTests.PostgresCteGrammarMatrix().Count() +
            MySqlGrammarMatrixTests.MySqlCteGrammarMatrix().Count() +
            SqlServerGrammarMatrixTests.SqlServerCteGrammarMatrix().Count() +
            SqliteGrammarMatrixTests.SqliteCteGrammarMatrix().Count() +
            OracleGrammarMatrixTests.OracleCteGrammarMatrix().Count() +
            FirebirdGrammarMatrixTests.FirebirdCteGrammarMatrix().Count();

        var positiveDml =
            DmlGrammarMatrixCases.ExpectedCaseCount +
            DialectNativeDmlCapabilityMatrixTests.NativeDmlCaseCount;

        var negativeQuery =
            NegativeGrammarMutationMatrixTests.UniversalMalformedGrammarMatrix().Count() +
            NegativeGrammarMutationMatrixTests.WrongDialectPostfixCastMatrix().Count() +
            NegativeGrammarMutationMatrixTests.WrongDialectRowLimitMatrix().Count();

        var negativeDml =
            NegativeDmlGrammarMutationMatrixTests.PolicyMutationMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.MalformedDmlMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.UpdateFromWrongSourceMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.DeleteUsingWrongSourceMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.FirebirdUpsertWrongSourceMatrix().Count();

        Assert.Equal(4485, positiveQuery);
        Assert.Equal(59, positiveDml);
        Assert.Equal(68, negativeQuery);
        Assert.Equal(44, negativeDml);
        Assert.Equal(
            4656,
            positiveQuery + positiveDml + negativeQuery + negativeDml);
    }
}
