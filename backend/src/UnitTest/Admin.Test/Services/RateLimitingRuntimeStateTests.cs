using Admin.Service.Models;
using Admin.Service.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Admin.Test.Services;

public class RateLimitingRuntimeStateTests
{
	[Fact]
	public void GetCurrent_ShouldReturnValuesFromConfiguration()
	{
		var configMock = new Mock<IConfiguration>();
		configMock.Setup(c => c["RateLimiting:PermitLimit"]).Returns("10");
		configMock.Setup(c => c["RateLimiting:WindowSeconds"]).Returns("60");
		configMock.Setup(c => c["RateLimiting:QueueLimit"]).Returns("5");

		var state = new RateLimitingRuntimeState(configMock.Object);
		var current = state.GetCurrent();

		Assert.Equal(10, current.PermitLimit);
		Assert.Equal(60, current.WindowSeconds);
		Assert.Equal(5, current.QueueLimit);
	}

	[Fact]
	public void SetCurrent_ShouldUpdateValues()
	{
		var configMock = new Mock<IConfiguration>();

		var state = new RateLimitingRuntimeState(configMock.Object);
		state.SetCurrent(new RateLimitingSettings
		{
			PermitLimit = 20,
			WindowSeconds = 120,
			QueueLimit = 10
		});

		var current = state.GetCurrent();

		Assert.Equal(20, current.PermitLimit);
		Assert.Equal(120, current.WindowSeconds);
		Assert.Equal(10, current.QueueLimit);
	}
}
