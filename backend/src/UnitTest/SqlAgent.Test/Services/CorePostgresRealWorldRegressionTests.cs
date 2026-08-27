using System.Text.RegularExpressions;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CorePostgresRealWorldRegressionTests
{
    [Fact]
    public void Compile_PostgresExtractQuarter_IsRepresentedByCanonicalDatePart()
    {
        const string sql =
            "SELECT EXTRACT(QUARTER FROM o.order_date) AS order_quarter, " +
            "COUNT(DISTINCT o.order_id) AS order_count, " +
            "SUM(od.unit_price * od.quantity * (1 - od.discount)) AS quarterly_sales " +
            "FROM public.orders o JOIN public.order_details od ON od.order_id = o.order_id " +
            "GROUP BY EXTRACT(QUARTER FROM o.order_date) ORDER BY order_quarter";

        var command = Compile(sql);

        Assert.Contains(
            "EXTRACT(QUARTER FROM",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresQuarter_IsStillFailClosedForUndeclaredTargets()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT EXTRACT(QUARTER FROM order_date) FROM orders",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("postgres-regression-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(
            "temporal.date_part.quarter",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresRepeatedParameterizedGroupExpression_ReusesParameterIdentity()
    {
        const string sql =
            "SELECT CASE " +
            "WHEN EXTRACT(MONTH FROM order_date) IN (1,2,3) THEN 'Q1' " +
            "WHEN EXTRACT(MONTH FROM order_date) IN (4,5,6) THEN 'Q2' " +
            "WHEN EXTRACT(MONTH FROM order_date) IN (7,8,9) THEN 'Q3' " +
            "ELSE 'Q4' END AS order_quarter, " +
            "COUNT(DISTINCT order_id) AS order_count, SUM(freight) AS total_freight " +
            "FROM public.orders " +
            "GROUP BY CASE " +
            "WHEN EXTRACT(MONTH FROM order_date) IN (1,2,3) THEN 'Q1' " +
            "WHEN EXTRACT(MONTH FROM order_date) IN (4,5,6) THEN 'Q2' " +
            "WHEN EXTRACT(MONTH FROM order_date) IN (7,8,9) THEN 'Q3' " +
            "ELSE 'Q4' END ORDER BY order_quarter";

        var command = Compile(sql);

        Assert.Equal(13, command.Parameters.Length);
        Assert.Contains("GROUP BY CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        foreach (var parameter in command.Parameters)
        {
            var exactParameterToken = new Regex(
                $@"(?<![A-Za-z0-9_]){Regex.Escape(parameter.Name)}(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant);
            Assert.Equal(2, exactParameterToken.Matches(command.Sql).Count);
        }
    }

    [Fact]
    public void Compile_PostgresGroupedAggregate_CanOrderWindowSpecification()
    {
        const string sql =
            "SELECT customer_id, ship_country, " +
            "SUM(net_order_value) AS country_sales, " +
            "ROW_NUMBER() OVER (" +
            "PARTITION BY customer_id " +
            "ORDER BY SUM(net_order_value) DESC) AS country_rank " +
            "FROM order_value GROUP BY customer_id, ship_country";

        var command = Compile(sql);

        Assert.Contains(
            "ROW_NUMBER() OVER",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ORDER BY SUM(",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(string sql) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                sql,
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-regression-v1"),
            new SqlExecutionPlanPolicy());
}
