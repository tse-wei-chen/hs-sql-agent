using System;
using System.Collections.Generic;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Versioned Oracle 21c+ duplicate-preserving set operators. Explicitly
/// require binary collation so Oracle's NLS_SORT=BINARY_CI/AI equivalence
/// substitution cannot silently change cross-provider row values.
/// </summary>
public sealed class SqlCoreOracle21SetAllCapabilityTests
{
    private static SqlProviderCapabilityProfile Oracle(int major, string? sort = "BINARY")
    {
        var settings = new Dictionary<string, string>();
        if (sort is not null) settings["NLS_SORT"] = sort;
        return new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle,
            new Version(major, 0),
            new HashSet<string>(),
            settings);
    }

    private static CompiledSqlCommand Compile(
        string sql, SqlAgentToolType source, SqlAgentToolType target,
        SqlProviderCapabilityProfile? sourceProfile = null,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, source, sourceProfile);
        return CoreSqlCompiler.CreateDefault().Compile(parsed, target,
            new SqlPlanValidationContext("oracle21-set-all-v1"),
            new SqlExecutionPlanPolicy(), targetProfile);
    }

    private static SqlCapability Find(
        SqlAgentToolType provider, SqlProviderCapabilityProfile? profile, string id) =>
        Assert.Single(SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            item => item.Id == id);

    [Theory]
    [InlineData("INTERSECT ALL", "set.intersect_all")]
    [InlineData("MINUS ALL", "set.except_all")]
    [InlineData("EXCEPT ALL", "set.except_all")]
    public void Oracle21RawSourceAndNativeTargetAdmitDuplicatePreservingSet(
        string op, string id)
    {
        var profile = Oracle(21);
        var compiled = Compile("SELECT id FROM alpha " + op + " SELECT id FROM beta",
            SqlAgentToolType.Oracle, SqlAgentToolType.Oracle, profile, profile);
        var expected = op == "EXCEPT ALL" ? "MINUS ALL" : op;
        Assert.Contains(expected, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" EXCEPT ALL ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SqlCapabilityStatus.Translated,
            Find(SqlAgentToolType.Oracle, profile, id).Status);
    }

    [Theory]
    [InlineData("INTERSECT ALL")]
    [InlineData("MINUS ALL")]
    [InlineData("EXCEPT ALL")]
    public void Oracle21SourceLowersToPostgresWithAllMultiplicityIntact(string op)
    {
        var result = Compile(
            "SELECT id FROM alpha " + op + " SELECT id FROM beta",
            SqlAgentToolType.Oracle, SqlAgentToolType.Postgres,
            Oracle(21));
        Assert.Contains(op == "INTERSECT ALL" ? " INTERSECT ALL " : " EXCEPT ALL ",
            result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" MINUS ALL ", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INTERSECT ALL")]
    [InlineData("EXCEPT ALL")]
    public void PostgresSourceLowersToOracle21NativeOperators(string op)
    {
        var result = Compile("SELECT id FROM alpha " + op + " SELECT id FROM beta",
            SqlAgentToolType.Postgres, SqlAgentToolType.Oracle,
            targetProfile: Oracle(21));
        Assert.Contains(op == "EXCEPT ALL" ? " MINUS ALL " : " INTERSECT ALL ",
            result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(19)]
    [InlineData(20)]
    public void OracleSourceWithoutProven21VersionCannotParseSetAll(int? major)
    {
        foreach (var sql in new[]
        {
            "SELECT 1 MINUS ALL SELECT 2",
            "SELECT 1 INTERSECT ALL SELECT 2",
            "SELECT 1 EXCEPT ALL SELECT 2"
        })
        {
            var error = Assert.ThrowsAny<Exception>(() =>
                CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle,
                    major is null ? null : Oracle(major.Value)));
            Assert.Contains("21", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(19)]
    [InlineData(20)]
    public void OracleTargetWithoutProven21VersionRejectsSetAll(int? major)
    {
        foreach (var op in new[] { "INTERSECT ALL", "EXCEPT ALL" })
        {
            var error = Assert.Throws<SqlCompilationException>(() =>
                Compile("SELECT 1 " + op + " SELECT 2",
                    SqlAgentToolType.Postgres, SqlAgentToolType.Oracle,
                    targetProfile: major is null ? null : Oracle(major.Value)));
            Assert.Contains(op == "INTERSECT ALL"
                ? "set.intersect_all" : "set.except_all",
                error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("BINARY_CI")]
    [InlineData("BINARY_AI")]
    public void OracleCollationWithoutBinaryProofStaysFailClosed(string? sort)
    {
        var profile = Oracle(21, sort);
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery("SELECT x FROM a MINUS ALL SELECT x FROM b",
                SqlAgentToolType.Oracle, profile));
        Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT x FROM a EXCEPT ALL SELECT x FROM b",
                SqlAgentToolType.Postgres, SqlAgentToolType.Oracle,
                targetProfile: profile));
        Assert.Equal(SqlCapabilityStatus.Rejected,
            Find(SqlAgentToolType.Oracle, profile, "set.except_all").Status);
    }

    [Fact]
    public void Oracle21SetAllMixedPrecedenceStillRefusesDangerousAssociativity()
    {
        var p = Oracle(21);
        Assert.ThrowsAny<Exception>(() =>
            Compile("SELECT 1 MINUS ALL SELECT 2 INTERSECT ALL SELECT 3",
                SqlAgentToolType.Oracle, SqlAgentToolType.Oracle, p, p));
        var grouped = Compile(
            "SELECT 1 MINUS ALL (SELECT 2 INTERSECT ALL SELECT 3)",
            SqlAgentToolType.Oracle, SqlAgentToolType.Oracle, p, p);
        Assert.Contains(" MINUS ALL (", grouped.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" INTERSECT ALL ", grouped.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oracle19PlainMinusRemainsAvailableWithoutVersionProfile()
    {
        var result = Compile("SELECT 1 MINUS SELECT 2",
            SqlAgentToolType.Oracle, SqlAgentToolType.Oracle);
        Assert.Contains(" MINUS ", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherTargetProvidersStillRejectDuplicatePreservingOperators(
        SqlAgentToolType provider)
    {
        Assert.Equal(SqlCapabilityStatus.Rejected,
            Find(provider, null, "set.intersect_all").Status);
        Assert.Equal(SqlCapabilityStatus.Rejected,
            Find(provider, null, "set.except_all").Status);
    }

    [Fact]
    public void ProfileCannotBeTransferredBetweenSourceAndTarget()
    {
        var p = Oracle(21);
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 1 MINUS ALL SELECT 2", SqlAgentToolType.Oracle));
        Assert.Throws<SqlCompilationException>(() =>
            Compile("SELECT 1 INTERSECT ALL SELECT 2",
                SqlAgentToolType.Oracle, SqlAgentToolType.Oracle,
                sourceProfile: p));
    }
}
