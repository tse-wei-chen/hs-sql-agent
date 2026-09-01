using System.Text.Json;
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
        var recursive = RecursiveCteGrammarMatrixTests.RecursiveCteGrammarMatrix().Count();
        var postgresNative =
            PostgresNativeCapabilityGrammarMatrixTests.PostgresNativeCapabilityMatrix().Count();
        var oracleNative =
            OracleNativeCapabilityGrammarMatrixTests.OracleNativeCapabilityMatrix().Count();
        var mySqlSession =
            MySqlSessionModeGrammarMatrixTests.ConcatSessionModeMatrix().Count()
            + MySqlSessionModeGrammarMatrixTests.AnsiQuotesSessionModeMatrix().Count();
        var sqlServerProfile =
            SqlServerProfileSensitiveGrammarMatrixTests.SqlServerStringAggregateProfileMatrix().Count();

        Assert.Equal(432, postgres);
        Assert.Equal(900, mySql);
        Assert.Equal(528, sqlServer);
        Assert.Equal(825, sqlite);
        Assert.Equal(900, oracle);
        Assert.Equal(900, firebird);
        Assert.Equal(128, recursive);
        Assert.Equal(72, postgresNative);
        Assert.Equal(72, oracleNative);
        Assert.Equal(48, mySqlSession);
        Assert.Equal(36, sqlServerProfile);
        Assert.Equal(
            4841,
            postgres + mySql + sqlServer + sqlite + oracle + firebird + recursive + postgresNative + oracleNative + mySqlSession + sqlServerProfile);
    }

    [Fact]
    public void NegativeGrammarMatrices_HaveStableCrossLayerFloor()
    {
        var queryGrammar =
            NegativeGrammarMutationMatrixTests.UniversalMalformedGrammarMatrix().Count()
            + NegativeGrammarMutationMatrixTests.WrongDialectPostfixCastMatrix().Count()
            + NegativeGrammarMutationMatrixTests.WrongDialectRowLimitMatrix().Count()
            + NegativeGrammarMutationMatrixTests.WrongDialectNullOrderingMatrix().Count()
            + NegativeRecursiveCteGrammarMutationMatrixTests.CommonRecursiveShapeMutationMatrix().Count()
            + NegativeRecursiveCteGrammarMutationMatrixTests.PortableSubsetMutationMatrix().Count()
            + NegativeRecursiveCteGrammarMutationMatrixTests.FirebirdUnionMutationMatrix().Count();
        var dmlGrammar =
            NegativeDmlGrammarMutationMatrixTests.PolicyMutationMatrix().Count()
            + NegativeDmlGrammarMutationMatrixTests.MalformedDmlMatrix().Count()
            + NegativeDmlGrammarMutationMatrixTests.UpdateFromWrongSourceMatrix().Count()
            + NegativeDmlGrammarMutationMatrixTests.DeleteUsingWrongSourceMatrix().Count()
            + NegativeDmlGrammarMutationMatrixTests.FirebirdUpsertWrongSourceMatrix().Count()
            + NegativeDmlGrammarMutationMatrixTests.WrongDialectPostfixCastDmlMatrix().Count()
            + NegativeDmlGrammarMutationMatrixTests.WrongDialectInsertSelectLimitMatrix().Count()
            + NegativeDmlGrammarMutationMatrixTests.CrossProviderDmlCapabilityMatrix().Count();
        var targetCapability =
            NegativeTargetCapabilityMatrixTests.NegativeTargetCapabilityMatrix().Count();
        var binding =
            NegativeBindingPolicyMatrixTests.BindingMutationMatrix().Count();
        var queryPolicy =
            NegativeBindingPolicyMatrixTests.QueryMaxRowsPolicyMatrix().Count();
        var lexical =
            NegativeLexicalMutationMatrixTests.NegativeLexicalMutationMatrix().Count();
        var tablePolicy =
            NegativeTablePolicyMatrixTests.TablePolicyMutationMatrix().Count();

        Assert.Equal(117, queryGrammar);
        Assert.Equal(68, dmlGrammar);
        Assert.Equal(18, targetCapability);
        Assert.Equal(30, binding);
        Assert.Equal(2, queryPolicy);
        Assert.Equal(48, lexical);
        Assert.Equal(24, tablePolicy);
        Assert.Equal(
            307,
            queryGrammar
            + dmlGrammar
            + targetCapability
            + binding
            + queryPolicy
            + lexical
            + tablePolicy);
    }

    [Fact]
    public void GeneratedParityCorpus_MatchesCrossDialectMatrixFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(4485, cases.Length);
        Assert.Equal(
            cases.Length,
            cases
                .Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());

        var counts = cases
            .GroupBy(
                item => item.GetProperty("dialect").GetString(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key!,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(432, counts["Postgres"]);
        Assert.Equal(900, counts["MySQL"]);
        Assert.Equal(528, counts["MsSqlServer"]);
        Assert.Equal(825, counts["Sqlite"]);
        Assert.Equal(900, counts["Oracle"]);
        Assert.Equal(900, counts["Firebird"]);
    }

    [Fact]
    public void GeneratedProfileSensitiveParityCorpus_MatchesSessionMatrixFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-profile-sensitive-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(84, cases.Length);
        Assert.Equal(
            84,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        var mySqlCases = cases
            .Where(item => string.Equals(
                item.GetProperty("dialect").GetString(),
                "MySQL",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sqlServerCases = cases
            .Where(item => string.Equals(
                item.GetProperty("dialect").GetString(),
                "MsSqlServer",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(48, mySqlCases.Length);
        Assert.Equal(36, sqlServerCases.Length);
        Assert.All(
            mySqlCases,
            item => Assert.True(
                item.TryGetProperty(
                    "sourceSessionModes",
                    out var modes)
                && modes.GetArrayLength() == 1));
        Assert.All(
            sqlServerCases,
            item => Assert.Equal(
                110,
                item.GetProperty(
                    "sourceCompatibilityLevel").GetInt32()));
    }

    [Fact]
    public void GeneratedOracleNativeParityCorpus_MatchesNativeMatrixFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-oracle-native-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(72, cases.Length);
        Assert.Equal(
            72,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(
            cases,
            item => Assert.Equal(
                "Oracle",
                item.GetProperty("dialect").GetString(),
                ignoreCase: true));
        Assert.All(
            cases,
            item => Assert.Equal(
                "12.1",
                item.GetProperty("sourceVersion").GetString()));
    }

    [Fact]
    public void GeneratedPostgresNativeParityCorpus_MatchesNativeMatrixFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-postgres-native-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(72, cases.Length);
        Assert.Equal(
            72,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(
            cases,
            item => Assert.Equal(
                "Postgres",
                item.GetProperty("dialect").GetString(),
                ignoreCase: true));
        Assert.All(
            cases,
            item => Assert.Equal(
                "13.0",
                item.GetProperty("sourceVersion").GetString()));
    }

    [Fact]
    public void GeneratedRecursiveParityCorpus_MatchesRecursiveMatrixFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-recursive-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(128, cases.Length);
        Assert.Equal(
            cases.Length,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());

        var counts = cases
            .GroupBy(
                item => item.GetProperty("dialect").GetString(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key!,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(32, counts["Postgres"]);
        Assert.Equal(32, counts["MySQL"]);
        Assert.Equal(32, counts["Sqlite"]);
        Assert.Equal(32, counts["Firebird"]);
    }
}
