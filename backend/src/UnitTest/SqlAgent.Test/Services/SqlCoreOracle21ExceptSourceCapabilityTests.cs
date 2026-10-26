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
/// Oracle 19c uses MINUS, not EXCEPT. Oracle 21c introduced EXCEPT;
/// source proof must not be inherited from a target-only profile.
/// </summary>
public sealed class SqlCoreOracle21ExceptSourceCapabilityTests
{
    private static SqlProviderCapabilityProfile Oracle(int major, string? sort = null)
    {
        var settings = new Dictionary<string, string>();
        if (sort is not null) settings["NLS_SORT"] = sort;
        return new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle,
            new Version(major, 0), new HashSet<string>(), settings);
    }

    private static CompiledSqlCommand Compile(string sql,
        SqlAgentToolType source, SqlAgentToolType target,
        SqlProviderCapabilityProfile? sourceProfile = null,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source, sourceProfile),
            target,
            new SqlPlanValidationContext("oracle-except-21c-v1"),
            new SqlExecutionPlanPolicy(), targetProfile);

    [Theory]
    [InlineData("SELECT 1 EXCEPT SELECT 2")]
    [InlineData("SELECT 1 EXCEPT SELECT 2 EXCEPT SELECT 3")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 EXCEPT SELECT 3")]
    [InlineData("SELECT 1 EXCEPT (SELECT 2 INTERSECT SELECT 3)")]
    [InlineData("SELECT id FROM a EXCEPT SELECT id FROM b")]
    [InlineData("WITH q AS (SELECT id FROM a) SELECT id FROM q EXCEPT SELECT id FROM b")]
    [InlineData("SELECT id FROM (SELECT id FROM a EXCEPT SELECT id FROM b) d")]
    public void Oracle21ExceptIsNativeMinusOnOracleAndExceptOnPostgres(string sql)
    {
        var sourceProfile = Oracle(21);
        var native = Compile(sql, SqlAgentToolType.Oracle, SqlAgentToolType.Oracle, sourceProfile);
        var portable = Compile(sql, SqlAgentToolType.Oracle, SqlAgentToolType.Postgres, sourceProfile);
        Assert.Contains(" MINUS ", native.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" EXCEPT ", native.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" EXCEPT ", portable.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void OraclePre21OrUnknownSourceCannotClaimExceptSyntax(int? major)
    {
        foreach (var sql in new[]
        {
            "SELECT 1 EXCEPT SELECT 2",
            "SELECT 1 EXCEPT ALL SELECT 2",
            "SELECT id FROM a EXCEPT SELECT id FROM b",
            "WITH q AS (SELECT 1) SELECT 1 EXCEPT SELECT 2",
            "SELECT 1 EXCEPT (SELECT 2 INTERSECT SELECT 3)"
        })
        {
            var error = Assert.ThrowsAny<Exception>(() =>
                CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle,
                    major is null ? null : Oracle(major.Value)));
            Assert.Contains("21", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(23)]
    public void PlainMinusRemainsAvailableToLegacyOracle(int version)
    {
        var compiled = Compile("SELECT 1 MINUS SELECT 2",
            SqlAgentToolType.Oracle, SqlAgentToolType.Oracle, Oracle(version));
        Assert.Contains(" MINUS ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oracle21ExceptDistinctNeedsVersionButNotBinaryCollation()
    {
        var p = Oracle(21, "BINARY_CI");
        var plain = Compile("SELECT 1 EXCEPT SELECT 2",
            SqlAgentToolType.Oracle, SqlAgentToolType.Oracle, p);
        Assert.Contains(" MINUS ", plain.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery("SELECT 1 EXCEPT ALL SELECT 2",
                SqlAgentToolType.Oracle, p));
    }

    [Fact]
    public void OracleTarget21VersionCannotAuthorizeLegacySourceExcept()
    {
        Assert.ThrowsAny<Exception>(() =>
            Compile("SELECT 1 EXCEPT SELECT 2",
                SqlAgentToolType.Oracle, SqlAgentToolType.Oracle,
                sourceProfile: Oracle(19), targetProfile: Oracle(21)));
    }

    [Fact]
    public void OldOracleTargetStillLowersPostgresExceptToMinus()
    {
        var cmd = Compile("SELECT 1 EXCEPT SELECT 2",
            SqlAgentToolType.Postgres, SqlAgentToolType.Oracle,
            targetProfile: Oracle(19));
        Assert.Contains(" MINUS ", cmd.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherSourcesKeepTheirExistingExceptGrammar(SqlAgentToolType provider)
    {
        Assert.NotNull(CoreSqlTextParser.ParseQuery("SELECT 1 EXCEPT SELECT 2", provider));
    }
}
