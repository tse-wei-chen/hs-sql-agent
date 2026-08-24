using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Strategies;

/// <summary>
/// Provider capability regressions belong to the canonical compiler. The strategy compatibility
/// compiler is intentionally obsolete and no longer owns translation diagnostics or passthrough
/// policy, so this suite asserts Core commands and fail-closed Core exceptions directly.
/// </summary>
public class SqlCapabilityCompilerTests
{
    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Firebird
    ];

    [Fact]
    public void Cast_CompilesForAllProvidersWithoutDroppingType()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT CAST(amount AS DECIMAL(12,2)) AS normalized_amount FROM orders");

        foreach (var provider in Providers)
        {
            var command = Compile(definition, provider, provider);
            Assert.Contains("CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DECIMAL(12,2)", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WindowFrame_AndLag_RespectProviderCapabilities()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT LAG(amount) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) FROM orders");

        foreach (var provider in Providers)
        {
            if (provider is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle)
            {
                var error = Assert.Throws<SqlCompilationException>(() => Compile(definition, provider, provider));
                Assert.Contains("window.frame.lag", error.Message, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var command = Compile(definition, provider, provider);
            Assert.Contains("LAG(", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ROWS BETWEEN 2 PRECEDING AND CURRENT ROW", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NullOrdering_FailsClosedOnlyForProvidersWithoutCapability()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT amount FROM orders ORDER BY amount DESC NULLS LAST");

        foreach (var provider in Providers)
        {
            if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
            {
                var error = Assert.Throws<SqlCompilationException>(() => Compile(definition, provider, provider));
                Assert.Contains("ordering.nulls", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Contains("NULLS LAST", Compile(definition, provider, provider).Sql, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Interval_IsStructuredAndPostgresOnly()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT created_at + INTERVAL '1 day' FROM events");

        var postgres = Compile(definition, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres);
        Assert.Contains("INTERVAL '1 day'", postgres.Sql, StringComparison.OrdinalIgnoreCase);

        foreach (var provider in Providers.Where(item => item != SqlAgentToolType.Postgres))
        {
            var error = Assert.Throws<SqlCompilationException>(() => Compile(definition, provider, provider));
            Assert.Contains("expression.interval", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CurrentTemporalKeywords_UseDeclaredProviderSemantics()
    {
        var currentDate = SqlDefinitionParser.ParseQuery("SELECT CURRENT_DATE AS value FROM orders");
        var currentTime = SqlDefinitionParser.ParseQuery("SELECT CURRENT_TIME AS value FROM orders");
        var currentTimestamp = SqlDefinitionParser.ParseQuery("SELECT CURRENT_TIMESTAMP AS value FROM orders");

        foreach (var provider in Providers)
        {
            Assert.Contains("CURRENT_TIMESTAMP", Compile(currentTimestamp, provider, provider).Sql, StringComparison.OrdinalIgnoreCase);

            var dateSql = Compile(currentDate, provider, provider).Sql;
            Assert.Contains(
                provider == SqlAgentToolType.MsSqlServer ? "CAST(CURRENT_TIMESTAMP AS date)" : "CURRENT_DATE",
                dateSql,
                StringComparison.OrdinalIgnoreCase);

            if (provider == SqlAgentToolType.Oracle)
            {
                var error = Assert.Throws<SqlCompilationException>(() => Compile(currentTime, provider, provider));
                Assert.Contains("CURRENT_TIME", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var timeSql = Compile(currentTime, provider, provider).Sql;
                Assert.Contains(
                    provider == SqlAgentToolType.MsSqlServer ? "CAST(CURRENT_TIMESTAMP AS time)" : "CURRENT_TIME",
                    timeSql,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DateDiff_Day_PreservesStartToEndSemanticsInSqlAndOrderedBindings()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATEDIFF(DAY, 20, 22) AS days FROM orders");

        foreach (var provider in Providers)
        {
            var command = Compile(definition, SqlAgentToolType.MsSqlServer, provider);
            Assert.DoesNotContain("DATEDIFF(\"DAY\"", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DATEDIFF([DAY]", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DATEDIFF(`DAY`", command.Sql, StringComparison.OrdinalIgnoreCase);

            if (provider is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite)
            {
                Assert.Equal(2, command.Parameters.Length);
                Assert.Equal(22, Convert.ToInt32(command.Parameters[0].Value));
                Assert.Equal(20, Convert.ToInt32(command.Parameters[1].Value));
            }
        }
    }

    [Fact]
    public void DateDiff_AndDateAdd_UnsupportedUnitsFailClosed()
    {
        var diff = SqlDefinitionParser.ParseQuery(
            "SELECT DATEDIFF(MONTH, DATE '2026-01-01', DATE '2026-08-01') FROM orders");
        var add = SqlDefinitionParser.ParseQuery(
            "SELECT DATEADD(MONTH, 2, DATE '2026-08-20') FROM orders");

        foreach (var provider in new[] { SqlAgentToolType.Postgres, SqlAgentToolType.Oracle, SqlAgentToolType.Sqlite })
        {
            Assert.Contains("core_date_diff.unit.month", Assert.Throws<SqlCompilationException>(() =>
                Compile(diff, SqlAgentToolType.MsSqlServer, provider)).Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("core_date_add.unit.month", Assert.Throws<SqlCompilationException>(() =>
                Compile(add, SqlAgentToolType.MsSqlServer, provider)).Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DateFormat_UsesExplicitSourceDialectAndNeverGuesses()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATE_FORMAT(created_at, '%Y-%m-%d %H:%i') FROM orders");
        var postgres = Compile(definition, SqlAgentToolType.MySQL, SqlAgentToolType.Postgres);
        Assert.Contains("YYYY-MM-DD HH24:MI", postgres.Sql, StringComparison.Ordinal);

        var wrongSource = Assert.Throws<SqlCompilationException>(() =>
            Compile(definition, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres));
        Assert.Contains("Postgres date-format token", wrongSource.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DateFormat_AndDateParse_FailClosedForUnsupportedProviders()
    {
        var format = SqlDefinitionParser.ParseQuery(
            "SELECT DATE_FORMAT(CURRENT_TIMESTAMP, 'yyyy-MM-dd') FROM orders");
        var parse = SqlDefinitionParser.ParseQuery(
            "SELECT TO_DATE('2026/08/22', 'yyyy/MM/dd') FROM orders");

        Assert.Contains("portable date formatting", Assert.Throws<SqlCompilationException>(() =>
            Compile(format, SqlAgentToolType.MsSqlServer, SqlAgentToolType.Firebird)).Message,
            StringComparison.OrdinalIgnoreCase);

        foreach (var provider in new[] { SqlAgentToolType.Sqlite, SqlAgentToolType.MsSqlServer })
        {
            Assert.Contains("function.date_parse", Assert.Throws<SqlCompilationException>(() =>
                Compile(parse, SqlAgentToolType.MsSqlServer, provider)).Message,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("formatted date parsing", Assert.Throws<SqlCompilationException>(() =>
            Compile(parse, SqlAgentToolType.MsSqlServer, SqlAgentToolType.Firebird)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoalesceFamily_UsesTargetSemanticRegistry()
    {
        var coalesce = SqlDefinitionParser.ParseQuery("SELECT COALESCE(customer_id, 0) FROM orders");
        Assert.Contains("IFNULL(", Compile(coalesce, SqlAgentToolType.Postgres, SqlAgentToolType.MySQL).Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ISNULL(", Compile(coalesce, SqlAgentToolType.Postgres, SqlAgentToolType.MsSqlServer).Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NVL(", Compile(coalesce, SqlAgentToolType.Postgres, SqlAgentToolType.Oracle).Sql, StringComparison.OrdinalIgnoreCase);

        var isNull = SqlDefinitionParser.ParseQuery("SELECT ISNULL(customer_id, 0) FROM orders");
        Assert.Contains("COALESCE(", Compile(isNull, SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres).Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownFunction_IsAlwaysFailClosed()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT MY_UDF(customer_id) FROM orders");
        foreach (var provider in Providers)
        {
            var error = Assert.Throws<SqlCompilationException>(() => Compile(definition, provider, provider));
            Assert.Contains("MY_UDF", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void JsonFunctions_UseStructuredProviderLowering()
    {
        var extract = SqlDefinitionParser.ParseQuery("SELECT JSON_EXTRACT(payload, '$.customer.name') FROM orders");
        Assert.Contains("JSONB_EXTRACT_PATH(", Compile(extract, SqlAgentToolType.MySQL, SqlAgentToolType.Postgres).Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON_EXTRACT(", Compile(extract, SqlAgentToolType.MySQL, SqlAgentToolType.Sqlite).Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SqlCompilationException>(() => Compile(extract, SqlAgentToolType.MySQL, SqlAgentToolType.Firebird));

        var set = SqlDefinitionParser.ParseQuery("SELECT JSON_SET(payload, '$.customer.name', 'Ada') FROM orders");
        Assert.Contains("JSON_MODIFY(", Compile(set, SqlAgentToolType.MySQL, SqlAgentToolType.MsSqlServer).Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSONB_SET(", Compile(set, SqlAgentToolType.MySQL, SqlAgentToolType.Postgres).Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SqlCompilationException>(() => Compile(set, SqlAgentToolType.MySQL, SqlAgentToolType.Oracle));
    }

    [Fact]
    public void RegexMatch_UsesRealPostgresOperatorAndFailsClosedWhereUnsupported()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT REGEXP_LIKE(customer_name, '^A') FROM orders");
        var postgres = Compile(definition, SqlAgentToolType.Oracle, SqlAgentToolType.Postgres);
        Assert.Contains(" ~ ", postgres.Sql, StringComparison.Ordinal);

        foreach (var provider in new[] { SqlAgentToolType.MsSqlServer, SqlAgentToolType.Sqlite, SqlAgentToolType.Firebird })
            Assert.Throws<SqlCompilationException>(() => Compile(definition, SqlAgentToolType.Oracle, provider));
    }

    [Fact]
    public void PostgresRound_WithScale_CastsInputToNumeric()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT ROUND(AVG(amount), 2) FROM orders");
        var command = Compile(definition, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres);
        Assert.Contains("ROUND(CAST(AVG(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS numeric)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScalarSubqueryProjection_PreservesAliasStructurally()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns =
            [
                new SubQuerySelectCondition
                {
                    TableName = "orders",
                    SelectColumns =
                    [
                        new FunctionSelectCondition
                        {
                            FunctionName = "COUNT",
                            Arguments = [new FieldSelectCondition { FieldName = "id" }]
                        }
                    ],
                    WhereColumnsAndValues =
                    [
                        new ColumnCompareWhereCondition
                        {
                            LeftFieldName = "user_id",
                            Operator = "=",
                            RightFieldName = "u.id"
                        }
                    ],
                    Alias = "order_count"
                }
            ]
        };

        foreach (var provider in Providers)
        {
            var command = Compile(definition, provider, provider);
            Assert.Contains("order_count", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FirebirdTemporalAndNumericLiterals_AreExplicitlyTyped()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new ConstantSelectCondition
                {
                    Constant = new SqlLocalDateTimeValue(new DateTime(2026, 8, 22, 13, 45, 9)),
                    Alias = "ts"
                },
                new ConstantSelectCondition { Constant = 1.25m, Alias = "amount" }
            ]
        };

        var command = Compile(definition, SqlAgentToolType.Firebird, SqlAgentToolType.Firebird);
        Assert.Contains("CAST(@p0 AS TIMESTAMP)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST(@p1 AS DECIMAL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityMatrix_MatchesCompilerBoundaries()
    {
        foreach (var provider in Providers)
        {
            var matrix = SqlCapabilityMatrix.ForProvider(provider);
            Assert.Equal(SqlCapabilityMatrix.Version, matrix.MatrixVersion);
            Assert.Equal(matrix.Capabilities.Count, matrix.Capabilities.Select(x => x.Id).Distinct().Count());

            Assert.Equal(
                provider == SqlAgentToolType.Postgres ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                Assert.Single(matrix.Capabilities, x => x.Id == "expression.interval").Status);
            Assert.Equal(
                provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                Assert.Single(matrix.Capabilities, x => x.Id == "ordering.nulls").Status);
        }
    }

    private static CompiledSqlCommand Compile(
        QueryDefinition definition,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            new ParsedStatement(QueryDefinitionCoreMapper.Map(definition), source),
            target,
            new SqlPlanValidationContext("capability-test"),
            new SqlExecutionPlanPolicy());
}
