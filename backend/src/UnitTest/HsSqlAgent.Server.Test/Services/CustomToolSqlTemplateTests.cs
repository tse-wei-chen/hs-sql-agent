using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class CustomToolSqlTemplateTests
{
    [Fact]
    public void Render_ShouldEscapeStringAsLiteral_NotSqlFragment()
    {
        var sql = CustomToolSqlTemplate.Render(
            "SELECT * FROM users WHERE email = {{email}}",
            """[{"name":"email","type":"string"}]""",
            new Dictionary<string, object?> { ["email"] = "x' OR 1=1 --" });

        Assert.Equal("SELECT * FROM users WHERE email = 'x'' OR 1=1 --'", sql);
    }

    [Theory]
    [InlineData("0 OR 1=1")]
    [InlineData("1; DROP TABLE users")]
    public void Render_ShouldRejectNonNumericNumber(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CustomToolSqlTemplate.Render(
            "SELECT * FROM users WHERE id = {{id}}",
            """[{"name":"id","type":"number"}]""",
            new Dictionary<string, object?> { ["id"] = value }));

        Assert.Contains("must be a number", exception.Message);
    }

    [Fact]
    public void Render_ShouldRejectPlaceholderInsideSqlString()
    {
        Assert.Throws<InvalidOperationException>(() => CustomToolSqlTemplate.Render(
            "SELECT * FROM users WHERE email = '{{email}}'",
            """[{"name":"email","type":"string"}]""",
            new Dictionary<string, object?> { ["email"] = "a@example.com" }));
    }

    [Theory]
    [InlineData("SELECT * FROM \"{{table}}\"")]
    [InlineData("SELECT * FROM `{{table}}`")]
    [InlineData("SELECT * FROM [{{table}}]")]
    public void Render_ShouldRejectIdentifierPlaceholders(string template)
    {
        Assert.Throws<InvalidOperationException>(() => CustomToolSqlTemplate.Render(
            template,
            """[{"name":"table","type":"string"}]""",
            new Dictionary<string, object?> { ["table"] = "users" }));
    }

    [Fact]
    public void Render_ShouldIgnoreQuotesInsideComments_WhenLocatingValuePlaceholder()
    {
        var sql = CustomToolSqlTemplate.Render(
            "-- user's filter\nSELECT * FROM users WHERE id = {{id}}",
            """[{"name":"id","type":"number"}]""",
            new Dictionary<string, object?> { ["id"] = 7 });

        Assert.EndsWith("WHERE id = 7", sql);
    }
}
