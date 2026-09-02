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
            FirebirdGrammarMatrixTests.FirebirdCteGrammarMatrix().Count() +
            RecursiveCteGrammarMatrixTests.RecursiveCteGrammarMatrix().Count() +
            PostgresNativeCapabilityGrammarMatrixTests.PostgresNativeCapabilityMatrix().Count() +
            PostgresQuotedFunctionGrammarMatrixTests.Matrix().Count() +
            PostgresQuotedFunctionGrammarMatrixTests.NativeQuotedSourceMatrix().Count() +
            PostgresQuotedFunctionGrammarMatrixTests.NativeQualificationMatrix().Count() +
            CrossDialectDistinctFromGrammarMatrixTests.PositiveMatrix().Count() +
            CrossDialectDistinctFromGrammarMatrixTests.NativeSourceMatrix().Count() +
            CrossDialectDistinctFromGrammarMatrixTests.CrossProviderTargetMatrix().Count() +
            OracleNativeCapabilityGrammarMatrixTests.OracleNativeCapabilityMatrix().Count() +
            MySqlSessionModeGrammarMatrixTests.ConcatSessionModeMatrix().Count() +
            MySqlSessionModeGrammarMatrixTests.AnsiQuotesSessionModeMatrix().Count() +
            SqlServerProfileSensitiveGrammarMatrixTests.SqlServerStringAggregateProfileMatrix().Count() +
            CrossDialectCastTypeGrammarMatrixTests.PositiveMatrix().Count() +
            CrossDialectCastTypeGrammarMatrixTests.PostgresPostfixMatrix().Count() +
            CrossDialectCastTypeGrammarMatrixTests.CrossProviderMatrix().Count() +
            TemporalDateMathGrammarMatrixTests.ExpectedPositiveCaseCount +
            AdvancedRegexGrammarMatrixTests.ExpectedPositiveCaseCount +
            AdvancedJsonPathGrammarMatrixTests.ExpectedPositiveCaseCount +
            MySqlJsonArrowGrammarMatrixTests.ExpectedPositiveCaseCount +
            PostgresJsonOperatorGrammarMatrixTests.ExpectedPositiveCaseCount;

        var positiveDml =
            DmlGrammarMatrixCases.ExpectedCaseCount +
            DialectNativeDmlCapabilityMatrixTests.NativeDmlCaseCount +
            DmlPredicateGrammarMatrixTests.UpdatePredicateGrammarMatrix().Count() +
            DmlPredicateGrammarMatrixTests.DeletePredicateGrammarMatrix().Count() +
            DmlPredicateGrammarMatrixTests.InsertSelectGrammarMatrix().Count();

        var negativeQuery =
            NegativeGrammarMutationMatrixTests.UniversalMalformedGrammarMatrix().Count() +
            NegativeGrammarMutationMatrixTests.WrongDialectPostfixCastMatrix().Count() +
            NegativeGrammarMutationMatrixTests.WrongDialectRowLimitMatrix().Count() +
            NegativeGrammarMutationMatrixTests.WrongDialectNullOrderingMatrix().Count() +
            PostgresQuotedFunctionGrammarMatrixTests.UnsupportedSourceMatrix().Count() +
            PostgresQuotedFunctionGrammarMatrixTests.OpaqueModifierNegativeMatrix().Count() +
            PostgresQuotedFunctionGrammarMatrixTests.Matrix().Count() +
            CrossDialectDistinctFromGrammarMatrixTests.UnsupportedSourceMatrix().Count() +
            CrossDialectDistinctFromGrammarMatrixTests.UnsupportedTargetMatrix().Count() +
            NegativeRecursiveCteGrammarMutationMatrixTests.CommonRecursiveShapeMutationMatrix().Count() +
            NegativeRecursiveCteGrammarMutationMatrixTests.PortableSubsetMutationMatrix().Count() +
            NegativeRecursiveCteGrammarMutationMatrixTests.FirebirdUnionMutationMatrix().Count() +
            CrossDialectCastTypeGrammarMatrixTests.NegativeMatrix().Count() +
            CrossDialectCastTypeGrammarMatrixTests.FirebirdSourceProfileNegativeMatrix().Count() +
            NegativeTemporalGrammarMutationMatrixTests.ExpectedCaseCount +
            AdvancedRegexGrammarMatrixTests.ExpectedNegativeCaseCount +
            AdvancedJsonPathGrammarMatrixTests.ExpectedNegativeCaseCount +
            MySqlJsonArrowGrammarMatrixTests.ExpectedNegativeCaseCount +
            PostgresJsonOperatorGrammarMatrixTests.ExpectedNegativeCaseCount;

        var negativeDml =
            NegativeDmlGrammarMutationMatrixTests.PolicyMutationMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.MalformedDmlMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.UpdateFromWrongSourceMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.DeleteUsingWrongSourceMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.OracleDeleteUsingVersionGateMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.SqlServerOutputWrongSourceMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.FirebirdUpsertWrongSourceMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.WrongDialectPostfixCastDmlMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.WrongDialectInsertSelectLimitMatrix().Count() +
            NegativeDmlGrammarMutationMatrixTests.CrossProviderDmlCapabilityMatrix().Count();

        Assert.Equal(5252, positiveQuery);
        Assert.Equal(434, positiveDml);
        Assert.Equal(319, negativeQuery);
        Assert.Equal(83, negativeDml);
        Assert.Equal(
            6088,
            positiveQuery + positiveDml + negativeQuery + negativeDml);
    }
}
