using WhatDo.StatusService.Mqtt;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class MqttOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void IsConfigured_WithoutHost_ReturnsFalse(string host)
    {
        var options = new MqttOptions { Host = host };

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_WithHost_ReturnsTrue()
    {
        var options = new MqttOptions { Host = "broker.example.com" };

        Assert.True(options.IsConfigured);
    }
}
