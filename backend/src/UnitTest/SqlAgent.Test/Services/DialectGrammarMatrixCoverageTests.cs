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
}
