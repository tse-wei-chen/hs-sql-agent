using SqlAgent.Service.Core.Analysis;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Normalization;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
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

        var ex = Assert.Throws<InvalidOperationException>(() =>
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

        var ex = Assert.Throws<InvalidOperationException>(() =>
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

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CoreSqlPlanValidator().Validate(
                canonical,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("expression.boolean_select", ex.Message);
    }

    private static CanonicalStatement Prepare(QueryDefinition definition, SqlAgentToolType target)
    {
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(definition), target);
        var bound = new SqlAstBinder().Bind(parsed);
        return CoreSqlNormalizer.CreateDefault().Normalize(bound, target);
    }
}
