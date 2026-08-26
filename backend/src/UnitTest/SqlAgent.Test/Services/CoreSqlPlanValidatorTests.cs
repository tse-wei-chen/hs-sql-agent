using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlPlanValidatorTests
{
    [Fact]
    public void Validate_UsesBinderFactsForWhitelist()
    {
        var canonical = Prepare(
            new QueryDefinition
            {
                TableName = "sales.orders",
                SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
            },
            SqlAgentToolType.Postgres);

        var validator = new CoreSqlPlanValidator();
        var plan = validator.Validate(
            canonical,
            new SqlPlanValidationContext(
                "policy-v1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sales.orders" }));

        Assert.Contains("sales.orders", plan.Facts.ReferencedTables);
        Assert.Equal("policy-v1", plan.PolicyVersion);
    }

    [Fact]
    public void Validate_RejectsPhysicalTableOutsideWhitelist()
    {
        var canonical = Prepare(
            new QueryDefinition
            {
                TableName = "sales.orders",
                SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
            },
            SqlAgentToolType.Postgres);

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            new CoreSqlPlanValidator().Validate(
                canonical,
                new SqlPlanValidationContext(
                    "policy-v1",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "crm.customers" })));

        Assert.Contains("sales.orders", ex.Message);
    }

    [Fact]
    public void Validate_RejectsNullOrderingForSqlServer()
    {
        var canonical = Prepare(
            new QueryDefinition
            {
                TableName = "users",
                SelectColumns = [new FieldSelectCondition { FieldName = "id" }],
                OrderByColumns =
                [
                    new FieldOrderByCondition
                    {
                        FieldName = "id",
                        NullOrdering = NullOrdering.First
                    }
                ]
            },
            SqlAgentToolType.MsSqlServer);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            new CoreSqlPlanValidator().Validate(
                canonical,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("ordering.nulls", ex.Message);
    }

    [Fact]
    public void Validate_RejectsIntervalForNonPostgresTarget()
    {
        var canonical = Prepare(
            new QueryDefinition
            {
                TableName = "events",
                SelectColumns =
                [
                    new OperationSelectCondition
                    {
                        Left = new FieldSelectCondition { FieldName = "created_at" },
                        Operator = ArithmeticOperator.Add,
                        Right = new IntervalSelectCondition { Literal = "1 day" }
                    }
                ]
            },
            SqlAgentToolType.MySQL);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            new CoreSqlPlanValidator().Validate(
                canonical,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("expression.interval", ex.Message);
    }

    [Fact]
    public void Validate_RejectsBooleanProjectionForOracle()
    {
        var canonical = Prepare(
            new QueryDefinition
            {
                TableName = "users",
                SelectColumns =
                [
                    new OperationSelectCondition
                    {
                        Left = new FieldSelectCondition { FieldName = "id" },
                        Operator = ArithmeticOperator.GreaterThan,
                        Right = new ConstantSelectCondition { Constant = 10 }
                    }
                ]
            },
            SqlAgentToolType.Oracle);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            new CoreSqlPlanValidator().Validate(
                canonical,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("expression.boolean_select", ex.Message);
    }

    [Fact]
    public void Validate_CanonicalizesModeledCteColumnAliases()
    {
        var canonical = PrepareSql(
            "WITH recent(id) AS (SELECT order_id FROM orders) SELECT id FROM recent",
            SqlAgentToolType.Postgres);

        var plan = new CoreSqlPlanValidator().Validate(
            canonical,
            new SqlPlanValidationContext("policy-v1"));

        var select = Assert.IsType<SelectStatement>(plan.Statement);
        var cte = Assert.Single(select.Ctes);
        Assert.Empty(cte.ColumnAliases);
        var cteSelect = Assert.IsType<SelectStatement>(cte.Query);
        Assert.Equal("id", Assert.Single(cteSelect.Select).Alias?.Value);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_CteColumnAliases_LowerThroughProjectionAliases(SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "WITH recent(id) AS (SELECT order_id FROM orders) SELECT id FROM recent",
            provider);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            provider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("WITH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recent", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WITH recent(id, total) AS (SELECT order_id FROM orders) SELECT id FROM recent", "declares 2 column alias")]
    [InlineData("WITH recent(id) AS (SELECT * FROM orders) SELECT id FROM recent", "contains a wildcard")]
    public void Compile_CteColumnAliases_WithUnknownOrMismatchedWidth_FailClosed(
        string sql,
        string expectedMessage)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CanonicalStatement Prepare(QueryDefinition definition, SqlAgentToolType target)
    {
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(definition), target);
        var bound = new SqlAstBinder().Bind(parsed);
        return CoreSqlNormalizer.CreateDefault().Normalize(bound, target);
    }

    private static CanonicalStatement PrepareSql(string sql, SqlAgentToolType target)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, target);
        var bound = new SqlAstBinder().Bind(parsed);
        return CoreSqlNormalizer.CreateDefault().Normalize(bound, target);
    }
}
