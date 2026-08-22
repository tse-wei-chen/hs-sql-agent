using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.SqlTranslation.Diagnostics;
using SqlAgent.Service.SqlTranslation.Functions;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class SqlCapabilityCompilerTests
{
    private readonly IReadOnlyList<BaseSqlStrategy> _strategies;

    public SqlCapabilityCompilerTests()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(x => x["McpKeySettings:HmacSecretKey"])
            .Returns("TestSecretKey12345678901234567890");
        var parser = new QueryValueParserService();
        _strategies =
        [
            new SqliteStrategy(parser, config.Object),
            new PostgresStrategy(parser, config.Object),
            new MySqlStrategy(parser, config.Object),
            new MsSqlServerStrategy(parser, config.Object),
            new OracleStrategy(parser, config.Object),
            new FirebirdStrategy(parser, config.Object)
        ];
    }

    [Fact]
    public void Cast_CompilesForAllProvidersWithoutDroppingType()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT CAST(amount AS DECIMAL(12,2)) AS normalized_amount FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQuerySql(definition);
            Assert.Contains("CAST(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DECIMAL(12,2)", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WindowFrame_CompilesForAllProvidersWithoutDroppingBounds()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQuerySql(definition);
            Assert.Contains("ROWS BETWEEN 2 PRECEDING AND CURRENT ROW", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NullOrdering_CompilesOnlyForDeclaredProviders()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT amount FROM orders ORDER BY amount DESC NULLS LAST");

        foreach (var strategy in _strategies)
        {
            if (strategy.DbType is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
            {
                var error = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
                Assert.Contains("NULLS FIRST/LAST", error.Message);
            }
            else
            {
                var sql = strategy.CompileQuerySql(definition);
                Assert.Contains("DESC NULLS LAST", sql, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Interval_CompilesOnlyForPostgres()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT created_at + INTERVAL '1 day' FROM events");

        foreach (var strategy in _strategies)
        {
            if (strategy.DbType == SqlAgentToolType.Postgres)
            {
                var sql = strategy.CompileQuerySql(definition);
                Assert.Contains("INTERVAL '1 day'", sql, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
                Assert.Contains("INTERVAL expressions", error.Message);
            }
        }
    }

    [Fact]
    public void CurrentTemporalKeywords_CompileWithDeclaredProviderSemantics()
    {
        var currentDate = SqlDefinitionParser.ParseQuery("SELECT CURRENT_DATE AS value FROM orders");
        var currentTime = SqlDefinitionParser.ParseQuery("SELECT CURRENT_TIME AS value FROM orders");
        var currentTimestamp = SqlDefinitionParser.ParseQuery("SELECT CURRENT_TIMESTAMP AS value FROM orders");

        foreach (var strategy in _strategies)
        {
            var timestampSql = strategy.CompileQuerySql(currentTimestamp);
            Assert.Contains("CURRENT_TIMESTAMP", timestampSql, StringComparison.OrdinalIgnoreCase);

            var dateSql = strategy.CompileQuerySql(currentDate);
            Assert.Contains(
                strategy.DbType == SqlAgentToolType.MsSqlServer ? "CAST(CURRENT_TIMESTAMP AS date)" : "CURRENT_DATE",
                dateSql,
                StringComparison.OrdinalIgnoreCase);

            if (strategy.DbType == SqlAgentToolType.Oracle)
            {
                var error = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(currentTime));
                Assert.Contains("CURRENT_TIME", error.Message);
            }
            else
            {
                var timeSql = strategy.CompileQuerySql(currentTime);
                Assert.Contains(
                    strategy.DbType == SqlAgentToolType.MsSqlServer ? "CAST(CURRENT_TIMESTAMP AS time)" : "CURRENT_TIME",
                    timeSql,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DateDiff_Day_CompilesWithStartToEndSemanticsForAllProviders()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATEDIFF(DAY, 20, 22) AS days FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SqlAgentToolType.MsSqlServer).Sql;
            Assert.DoesNotContain("DATEDIFF(\"DAY\"", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DATEDIFF([DAY]", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DATEDIFF(`DAY`", sql, StringComparison.OrdinalIgnoreCase);

            if (strategy.DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite)
            {
                var endIndex = sql.IndexOf("22", StringComparison.OrdinalIgnoreCase);
                var startIndex = sql.IndexOf("20", StringComparison.OrdinalIgnoreCase);
                Assert.True(endIndex >= 0 && startIndex > endIndex, sql);
            }
        }
    }

    [Fact]
    public void DateDiff_UnsupportedOrUnsafeUnit_FailsClosed()
    {
        var monthDefinition = SqlDefinitionParser.ParseQuery(
            "SELECT DATEDIFF(MONTH, DATE '2026-01-01', DATE '2026-08-01') FROM orders");
        var unsafeDefinition = SqlDefinitionParser.ParseQuery(
            "SELECT DATEDIFF(customer_id, DATE '2026-01-01', DATE '2026-08-01') FROM orders");

        foreach (var strategy in _strategies.Where(x =>
                     x.DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite))
        {
            var unitError = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(monthDefinition));
            Assert.Contains("DATEDIFF unit MONTH", unitError.Message);
        }

        foreach (var strategy in _strategies)
        {
            var unsafeError = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(unsafeDefinition));
            Assert.Contains("Unsupported DATEADD/DATEDIFF date-part unit", unsafeError.Message);
        }
    }

    [Fact]
    public void DateAdd_Day_CompilesForAllProvidersWithoutQuotingUnit()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATEADD(DAY, 2, DATE '2026-08-20') AS due_date FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SqlAgentToolType.MsSqlServer).Sql;
            Assert.DoesNotContain("\"DAY\"", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[DAY]", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("`DAY`", sql, StringComparison.OrdinalIgnoreCase);

            if (strategy.DbType == SqlAgentToolType.MySQL)
                Assert.Contains("TIMESTAMPADD(DAY", sql, StringComparison.OrdinalIgnoreCase);
            if (strategy.DbType == SqlAgentToolType.Postgres)
                Assert.Contains("INTERVAL '1 day'", sql, StringComparison.OrdinalIgnoreCase);
            if (strategy.DbType == SqlAgentToolType.Sqlite)
                Assert.Contains("PRINTF('%+d day'", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DateAdd_UnimplementedProviderUnit_FailsClosed()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATEADD(MONTH, 2, DATE '2026-08-20') FROM orders");

        foreach (var strategy in _strategies.Where(x =>
                     x.DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite))
        {
            var error = Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
            Assert.Contains("DATEADD unit MONTH", error.Message);
        }
    }

    [Fact]
    public void DateFormat_UsesDifferentMinuteTokensForSqliteAndMySql()
    {
        var namedFormat = SqlDefinitionParser.ParseQuery(
            "SELECT DATE_FORMAT(TIMESTAMP '2026-08-22 13:45:09', 'yyyy-MM-dd HH:mm:ss') FROM orders");
        var mysqlFormat = SqlDefinitionParser.ParseQuery(
            "SELECT DATE_FORMAT(TIMESTAMP '2026-08-22 13:45:09', '%Y-%m-%d %H:%i:%S') FROM orders");

        var sqlite = _strategies.Single(x => x.DbType == SqlAgentToolType.Sqlite);
        var mysql = _strategies.Single(x => x.DbType == SqlAgentToolType.MySQL);

        Assert.Contains("%Y-%m-%d %H:%M:%S", sqlite.CompileQueryTranslation(namedFormat, SqlAgentToolType.MsSqlServer).Sql);
        Assert.Contains("%Y-%m-%d %H:%M:%S", sqlite.CompileQueryTranslation(mysqlFormat, SqlAgentToolType.MySQL).Sql);
        Assert.Contains("%Y-%m-%d %H:%i:%S", mysql.CompileQueryTranslation(namedFormat, SqlAgentToolType.MsSqlServer).Sql);
        Assert.Contains("%Y-%m-%d %H:%i:%S", mysql.CompileQueryTranslation(mysqlFormat, SqlAgentToolType.MySQL).Sql);
    }

    [Fact]
    public void DateFormat_FirebirdFailsClosedBeforeExecution()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATE_FORMAT(CURRENT_TIMESTAMP, 'yyyy-MM-dd') FROM orders");
        var firebird = _strategies.Single(x => x.DbType == SqlAgentToolType.Firebird);

        var error = Assert.Throws<InvalidOperationException>(() =>
            firebird.CompileQueryTranslation(definition, SqlAgentToolType.MsSqlServer));
        Assert.Contains("portable date formatting", error.Message);
    }

    [Fact]
    public void ToDate_CompilesOnlyForProvidersWithADeclaredTranslation()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT TO_DATE('2026/08/22', 'yyyy/MM/dd') FROM orders");

        foreach (var strategy in _strategies)
        {
            if (strategy.DbType is SqlAgentToolType.Sqlite or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird)
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    strategy.CompileQueryTranslation(definition, SqlAgentToolType.MsSqlServer));
                Assert.Contains("formatted date parsing", error.Message);
            }
            else
            {
                var sql = strategy.CompileQueryTranslation(definition, SqlAgentToolType.MsSqlServer).Sql;
                Assert.Contains(
                    strategy.DbType == SqlAgentToolType.MySQL ? "%Y/%m/%d" : "YYYY/MM/DD",
                    sql,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryMetadataFunctionTemplate_IsAcceptedByTemplateGrammar()
    {
        foreach (var definition in FunctionDefinitionLoader.LoadEmbedded()
                     .Where(item => item.TranslationKind == FunctionTranslationKind.Template))
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Template));
            Assert.NotNull(new FunctionTemplateEngine(definition.Template!).Parse());
        }
    }

    [Fact]
    public void DatePartExtraction_UsesNumericExpressionsAcrossProviders()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT YEAR(DATE '2026-08-22'), MONTH(DATE '2026-08-22'), DAY(DATE '2026-08-22') FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQuerySql(definition);
            if (strategy.DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
                Assert.Contains("EXTRACT(YEAR FROM", sql, StringComparison.OrdinalIgnoreCase);
            if (strategy.DbType == SqlAgentToolType.Firebird)
                Assert.Contains("CAST(? AS DATE)", sql, StringComparison.OrdinalIgnoreCase);
            if (strategy.DbType == SqlAgentToolType.Sqlite)
                Assert.Contains("CAST(STRFTIME('%Y'", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("LEN")]
    [InlineData("LENGTH")]
    [InlineData("CHAR_LENGTH")]
    public void StringLength_UsesProductionSemanticRegistry(string sourceName)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {sourceName}(customer_id) FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(sourceName)).Sql;
            var expected = strategy.DbType switch
            {
                SqlAgentToolType.MsSqlServer => "LEN(",
                SqlAgentToolType.MySQL or SqlAgentToolType.Firebird => "CHAR_LENGTH(",
                _ => "LENGTH("
            };
            Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("CEIL")]
    [InlineData("CEILING")]
    public void Ceiling_UsesProductionSemanticRegistry(string sourceName)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {sourceName}(1.2) FROM orders");

        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(sourceName)).Sql;
            Assert.Contains(
                strategy.DbType == SqlAgentToolType.MsSqlServer ? "CEILING(" : "CEIL(",
                sql,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CompileQueryTranslation_UnknownFunctionReturnsPassthroughDiagnostic()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT MY_COMPANY_RISK_SCORE(customer_id) FROM orders");
        var strategy = Assert.IsAssignableFrom<BaseSqlStrategy>(
            _strategies.Single(item => item.DbType == SqlAgentToolType.Postgres));

        var result = strategy.CompileQueryTranslation(
            definition,
            SqlAgentToolType.MsSqlServer,
            UnknownFunctionPolicy.WarnAndPassthrough);

        Assert.Contains("MY_COMPANY_RISK_SCORE", result.Sql);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("SQLFUNC001", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(FunctionPortability.Unknown, diagnostic.Portability);
    }

    [Fact]
    public void CompileQueryTranslation_DefaultPolicyRejectsUnknownFunction()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT MY_UDF(customer_id) FROM orders");
        var strategy = Assert.IsAssignableFrom<BaseSqlStrategy>(_strategies[0]);

        Assert.Throws<InvalidOperationException>(() => strategy.CompileQueryTranslation(
            definition, SqlAgentToolType.MsSqlServer));
    }

    [Fact]
    public void CompileQuerySql_AgentPathRejectsUnknownFunctionButAllowsPortableAggregate()
    {
        var strategy = Assert.IsAssignableFrom<BaseSqlStrategy>(
            _strategies.Single(item => item.DbType == SqlAgentToolType.Postgres));
        var unknown = SqlDefinitionParser.ParseQuery("SELECT MY_UDF(customer_id) FROM orders");
        var aggregate = SqlDefinitionParser.ParseQuery("SELECT COUNT(customer_id) FROM orders");

        Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(unknown));
        Assert.Contains("COUNT(", strategy.CompileQuerySql(aggregate), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileQuerySql_UsesExplicitSourceDialectFromQueryContract()
    {
        var strategy = Assert.IsAssignableFrom<BaseSqlStrategy>(
            _strategies.Single(item => item.DbType == SqlAgentToolType.Postgres));
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATE_FORMAT(created_at, '%Y-%m-%d %H:%i') FROM orders");
        definition.SourceDialect = SqlAgentToolType.MySQL;

        Assert.Contains("YYYY-MM-DD HH24:MI", strategy.CompileQuerySql(definition), StringComparison.Ordinal);
    }

    [Fact]
    public void CompileQuerySql_OmittedSourceDialectMeansTargetDialectAndDoesNotGuess()
    {
        var strategy = Assert.IsAssignableFrom<BaseSqlStrategy>(
            _strategies.Single(item => item.DbType == SqlAgentToolType.Postgres));
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DATE_FORMAT(created_at, '%Y-%m-%d') FROM orders");

        var error = Assert.Throws<FormatException>(() => strategy.CompileQuerySql(definition));
        Assert.Contains("Postgres date-format token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileQueryTranslation_ThrowPolicyRejectsUnknownFunction()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT MY_UDF(customer_id) FROM orders");
        var strategy = Assert.IsAssignableFrom<BaseSqlStrategy>(_strategies[0]);

        var error = Assert.Throws<InvalidOperationException>(() => strategy.CompileQueryTranslation(
            definition,
            SqlAgentToolType.MsSqlServer,
            UnknownFunctionPolicy.Throw));

        Assert.Contains("MY_UDF", error.Message);
    }

    [Theory]
    [InlineData("IFNULL")]
    [InlineData("NVL")]
    [InlineData("ISNULL")]
    [InlineData("COALESCE")]
    public void CoalesceFamily_UsesSemanticRegistry(string sourceName)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {sourceName}(customer_id, 0) FROM orders");
        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(sourceName)).Sql;
            var expected = strategy.DbType switch
            {
                SqlAgentToolType.MsSqlServer => "ISNULL(",
                SqlAgentToolType.MySQL => "IFNULL(",
                SqlAgentToolType.Oracle => "NVL(",
                _ => "COALESCE("
            };
            Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("RAND")]
    [InlineData("RANDOM")]
    public void RandomFamily_UsesSemanticRegistry(string sourceName)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {sourceName}() FROM orders");
        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(sourceName)).Sql;
            Assert.Contains(
                strategy.DbType is SqlAgentToolType.Postgres or SqlAgentToolType.Sqlite ? "RANDOM(" : "RAND(",
                sql,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("REPEAT")]
    [InlineData("REPLICATE")]
    public void RepeatFamily_UsesSemanticRegistryForSupportedTargets(string sourceName)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {sourceName}('x', 3) FROM orders");
        foreach (var strategy in _strategies.Where(item =>
                     item.DbType is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Postgres or SqlAgentToolType.MySQL))
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(sourceName)).Sql;
            Assert.Contains(
                strategy.DbType == SqlAgentToolType.MsSqlServer ? "REPLICATE(" : "REPEAT(",
                sql,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("NOW")]
    [InlineData("GETDATE")]
    [InlineData("SYSDATE")]
    public void CurrentTimestampFamily_RendersAsControlledKeyword(string sourceName)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {sourceName}() FROM orders");
        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(sourceName)).Sql;
            Assert.Contains("CURRENT_TIMESTAMP", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CURRENT_TIMESTAMP()", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("GROUP_CONCAT")]
    [InlineData("STRING_AGG")]
    [InlineData("LISTAGG")]
    [InlineData("LIST")]
    public void StringAggregateFamily_UsesSemanticRegistryAndDefaultSeparator(string sourceName)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {sourceName}(customer_id) FROM orders");
        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(sourceName)).Sql;
            var expected = strategy.DbType switch
            {
                SqlAgentToolType.MsSqlServer or SqlAgentToolType.Postgres => "STRING_AGG(",
                SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite => "GROUP_CONCAT(",
                SqlAgentToolType.Oracle => "LISTAGG(",
                _ => "LIST("
            };
            Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
            if (strategy.DbType is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Postgres
                or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
                Assert.Contains("','", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Strategies_DoNotDeclareLegacyFunctionMappingsOrTemplates()
    {
        foreach (var strategy in _strategies)
        {
            var declared = strategy.GetType().GetProperty(
                "FunctionNameMappings",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);
            Assert.Null(declared);
            var templates = strategy.GetType().GetProperty(
                "FunctionTemplates",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);
            Assert.Null(templates);
        }
    }

    [Fact]
    public void CoreExpressionProperties_AreInitOnly()
    {
        var properties = new[]
        {
            typeof(FieldSelectCondition).GetProperty(nameof(FieldSelectCondition.FieldName))!,
            typeof(OperationSelectCondition).GetProperty(nameof(OperationSelectCondition.Left))!,
            typeof(OperationSelectCondition).GetProperty(nameof(OperationSelectCondition.Operator))!,
            typeof(OperationSelectCondition).GetProperty(nameof(OperationSelectCondition.Right))!,
            typeof(ConstantSelectCondition).GetProperty(nameof(ConstantSelectCondition.Constant))!,
            typeof(CastSelectCondition).GetProperty(nameof(CastSelectCondition.Expression))!,
            typeof(CastSelectCondition).GetProperty(nameof(CastSelectCondition.TypeName))!,
            typeof(IntervalSelectCondition).GetProperty(nameof(IntervalSelectCondition.Literal))!,
            typeof(FunctionSelectCondition).GetProperty(nameof(FunctionSelectCondition.FunctionName))!,
            typeof(FunctionSelectCondition).GetProperty(nameof(FunctionSelectCondition.Arguments))!,
            typeof(FunctionSelectCondition).GetProperty(nameof(FunctionSelectCondition.IsDistinct))!,
            typeof(FunctionSelectCondition).GetProperty(nameof(FunctionSelectCondition.FilterWhereConditions))!,
            typeof(FunctionSelectCondition).GetProperty(nameof(FunctionSelectCondition.Window))!,
            typeof(CaseWhenSelectCondition).GetProperty(nameof(CaseWhenSelectCondition.CaseWhen))!,
            typeof(CaseWhenSelectCondition).GetProperty(nameof(CaseWhenSelectCondition.ElseValue))!
        };

        foreach (var property in properties)
        {
            Assert.Contains(
                typeof(System.Runtime.CompilerServices.IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }
    }

    [Fact]
    public void JsonExtract_UsesSpecializedDialectRenderers()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT JSON_EXTRACT(payload, '$.customer.name') FROM orders");
        foreach (var strategy in _strategies)
        {
            if (strategy.DbType is SqlAgentToolType.Firebird or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle)
            {
                Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
                continue;
            }
            var sql = strategy.CompileQuerySql(definition);
            var expected = strategy.DbType switch
            {
                SqlAgentToolType.Postgres => "JSONB_EXTRACT_PATH(",
                _ => "JSON_EXTRACT("
            };
            Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void JsonSet_UsesSpecializedDialectRenderersAndFailsClosedWhenUnsupported()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT JSON_SET(payload, '$.customer.name', 'Ada') FROM orders");
        foreach (var strategy in _strategies)
        {
            if (strategy.DbType is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird)
            {
                Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
                continue;
            }
            var sql = strategy.CompileQuerySql(definition);
            var expected = strategy.DbType switch
            {
                SqlAgentToolType.MsSqlServer => "JSON_MODIFY(",
                SqlAgentToolType.Postgres => "JSONB_SET(",
                _ => "JSON_SET("
            };
            Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RegexMatch_UsesSupportedDialectFunctionAndFailsClosedOtherwise()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT REGEXP_LIKE(customer_name, '^A') FROM orders");
        foreach (var strategy in _strategies)
        {
            if (strategy.DbType is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite or SqlAgentToolType.Firebird)
            {
                Assert.Throws<InvalidOperationException>(() => strategy.CompileQuerySql(definition));
                continue;
            }
            Assert.Contains("REGEXP_LIKE(", strategy.CompileQuerySql(definition), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("STRPOS", "customer_name, 'A'")]
    [InlineData("INSTR", "customer_name, 'A'")]
    [InlineData("LOCATE", "'A', customer_name")]
    [InlineData("CHARINDEX", "'A', customer_name")]
    public void PositionFamily_NormalizesArgumentOrder(string functionName, string arguments)
    {
        var definition = SqlDefinitionParser.ParseQuery($"SELECT {functionName}({arguments}) FROM orders");
        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SourceDialectFor(functionName)).Sql;
            var expected = strategy.DbType switch
            {
                SqlAgentToolType.MsSqlServer => "CHARINDEX(",
                SqlAgentToolType.Postgres => "STRPOS(",
                SqlAgentToolType.MySQL => "LOCATE(",
                SqlAgentToolType.Firebird => "POSITION(",
                _ => "INSTR("
            };
            Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void HavingFunction_UsesSameSemanticPipelineAsSelect()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT customer_id FROM orders GROUP BY customer_id HAVING LEN(customer_id) > 2");
        foreach (var strategy in _strategies)
        {
            var sql = strategy.CompileQueryTranslation(definition, SqlAgentToolType.MsSqlServer).Sql;
            var expected = strategy.DbType switch
            {
                SqlAgentToolType.MsSqlServer => "LEN(",
                SqlAgentToolType.MySQL or SqlAgentToolType.Firebird => "CHAR_LENGTH(",
                _ => "LENGTH("
            };
            Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TranslationDiagnostics_ReportEquivalentAndEmulatedFunctions()
    {
        var strategy = Assert.IsAssignableFrom<BaseSqlStrategy>(
            _strategies.Single(item => item.DbType == SqlAgentToolType.Postgres));
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT LEN(customer_id), DATEADD(DAY, 2, created_at) FROM orders");

        var result = strategy.CompileQueryTranslation(definition, SqlAgentToolType.MsSqlServer);

        Assert.Contains(result.Diagnostics, item =>
            item.Code == "SQLFUNC002" && item.Portability == FunctionPortability.Equivalent);
        Assert.Contains(result.Diagnostics, item =>
            item.Code == "SQLFUNC002" && item.Portability == FunctionPortability.Emulated);
    }

    [Fact]
    public void CapabilityMatrix_MatchesProviderSpecificCompilerBoundaries()
    {
        foreach (var strategy in _strategies)
        {
            var matrix = SqlCapabilityMatrix.ForProvider(strategy.DbType);
            Assert.Equal(SqlCapabilityMatrix.Version, matrix.MatrixVersion);
            Assert.Equal(matrix.Capabilities.Count, matrix.Capabilities.Select(x => x.Id).Distinct().Count());

            var interval = Assert.Single(matrix.Capabilities, x => x.Id == "expression.interval");
            Assert.Equal(
                strategy.DbType == SqlAgentToolType.Postgres ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                interval.Status);

            var nulls = Assert.Single(matrix.Capabilities, x => x.Id == "ordering.nulls");
            Assert.Equal(
                strategy.DbType is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                nulls.Status);

            var standaloneTime = Assert.Single(matrix.Capabilities, x => x.Id == "temporal.standalone_time");
            Assert.Equal(
                strategy.DbType == SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Translated,
                standaloneTime.Status);

            var offsetTimestamp = Assert.Single(matrix.Capabilities, x => x.Id == "temporal.offset_timestamp");
            Assert.Equal(
                strategy.DbType == SqlAgentToolType.MySQL
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Translated,
                offsetTimestamp.Status);

            var formattedParse = Assert.Single(matrix.Capabilities, x => x.Id == "temporal.formatted_parse");
            Assert.Equal(
                strategy.DbType is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                formattedParse.Status);

            var jsonExtract = Assert.Single(matrix.Capabilities, x => x.Id == "json.extract");
            Assert.Equal(
                strategy.DbType is SqlAgentToolType.Firebird or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle
                    ? SqlCapabilityStatus.Rejected : SqlCapabilityStatus.Translated,
                jsonExtract.Status);
        }
    }

    private static SqlAgentToolType SourceDialectFor(string functionName) =>
        functionName.ToUpperInvariant() switch
        {
            "LEN" or "CEILING" or "ISNULL" or "REPLICATE" or "GETDATE" or "CHARINDEX" => SqlAgentToolType.MsSqlServer,
            "IFNULL" or "RAND" or "GROUP_CONCAT" or "LOCATE" => SqlAgentToolType.MySQL,
            "NVL" or "SYSDATE" or "LISTAGG" or "INSTR" => SqlAgentToolType.Oracle,
            "CHAR_LENGTH" or "LIST" => SqlAgentToolType.Firebird,
            _ => SqlAgentToolType.Postgres
        };
}
