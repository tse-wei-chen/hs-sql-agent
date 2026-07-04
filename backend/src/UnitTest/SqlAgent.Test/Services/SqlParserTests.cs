using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Validation;
using Xunit;

namespace SqlAgent.Test.Services;

public class SqlParserTests
{
    [Fact]
    public void Parse_PostgresDateCastInSelectAndHaving_ReturnsQueryDefinition()
    {
        const string sql = """
            WITH SystemMax AS (
                SELECT MAX(order_date) AS max_system_date FROM orders
            )
            SELECT
                c.customer_id,
                c.company_name,
                c.contact_name,
                c.phone,
                sm.max_system_date,
                MAX(o.order_date) AS last_order_date,
                (sm.max_system_date::date - MAX(o.order_date)::date) AS days_since_last_order
            FROM customers c
            LEFT JOIN orders o ON c.customer_id = o.customer_id
            CROSS JOIN SystemMax sm
            GROUP BY
                c.customer_id,
                c.company_name,
                c.contact_name,
                c.phone,
                sm.max_system_date
            HAVING
                (sm.max_system_date::date - MAX(o.order_date)::date) > 180
                OR MAX(o.order_date) IS NULL
            ORDER BY days_since_last_order DESC;
            """;

        var qd = new SqlParser(new SqlTokenizer(sql).Tokenize()).Parse();
        var errors = DefinitionValidator.Validate(qd);

        Assert.Empty(errors);
        Assert.Equal("customers", qd.TableName);
        Assert.Equal("c", qd.Alias);
        Assert.NotNull(qd.CteConditions);
        Assert.Single(qd.CteConditions);
        Assert.Equal("SystemMax", qd.CteConditions[0].CteAliasName);
        Assert.NotNull(qd.Joins);
        Assert.Equal(2, qd.Joins.Count);
        Assert.Equal(JoinType.Cross, qd.Joins[1].Type);
        Assert.Empty(qd.Joins[1].OnConditions);
        Assert.NotNull(qd.SelectColumns);
        var dateDiffColumn = Assert.IsType<OperationSelectCondition>(qd.SelectColumns[6]);
        Assert.Equal("days_since_last_order", dateDiffColumn.Alias);
        Assert.NotNull(qd.HavingConditions);
        var havingGroup = Assert.IsType<GroupHavingCondition>(qd.HavingConditions[0]);
        var expressionHaving = Assert.IsType<ExpressionHavingCondition>(havingGroup.Groups[0]);
        Assert.IsType<OperationSelectCondition>(expressionHaving.LeftExpression);
        Assert.Equal(">", expressionHaving.Operator);
    }
}
