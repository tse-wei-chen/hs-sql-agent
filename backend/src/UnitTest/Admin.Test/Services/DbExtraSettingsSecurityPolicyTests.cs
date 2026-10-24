using Admin.Service.Services;
using Xunit;

namespace Admin.Test.Services;

public class DbExtraSettingsSecurityPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"TrustServerCertificate\":true,\"Encrypt\":true}")]
    [InlineData("{\"extended\":{\"sslmode\":\"verify-full\"}}")]
    public void ValidNonSecretSettingsAreAllowed(string? value)
        => DbExtraSettingsSecurityPolicy.Validate(value);

    [Theory]
    [InlineData("{\"Password\":\"do-not-leak\"}")]
    [InlineData("{\"api_key\":\"do-not-leak\"}")]
    [InlineData("{\"nested\":{\"authenticationToken\":\"do-not-leak\"}}")]
    [InlineData("{\"array\":[{\"clientSecret\":\"do-not-leak\"}]}")]
    [InlineData("{\"info\":\"Password=do-not-leak;Host=db\"}")]
    [InlineData("{\"info\":\"token: do-not-leak\"}")]
    [InlineData("{\"connection-string\":\"Host=db\"}")]
    public void CredentialSettingsAreRejectedWithoutEchoingInput(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => DbExtraSettingsSecurityPolicy.Validate(value));
        Assert.DoesNotContain("do-not-leak", ex.Message);
        Assert.DoesNotContain("authenticationToken", ex.Message);
        Assert.DoesNotContain("clientSecret", ex.Message);
    }

    [Theory]
    [InlineData("Server=local;Password=abc")]
    [InlineData("[{\"Encrypt\":true}]")]
    [InlineData("null")]
    [InlineData("{ invalid")]
    public void MalformedOrNonObjectInputFailsClosed(string value)
        => Assert.Throws<ArgumentException>(() => DbExtraSettingsSecurityPolicy.Validate(value));

    [Fact]
    public void TooLargeSettingsFailWithoutEchoingContent()
    {
        var value = "{\"note\":\"" + new string('x', 20000) + "\"}";
        var ex = Assert.Throws<ArgumentException>(() => DbExtraSettingsSecurityPolicy.Validate(value));
        Assert.DoesNotContain(new string('x', 50), ex.Message);
    }
}
