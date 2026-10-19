using WhatDo.StatusService.Mqtt;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class MqttTopicsTests
{
    [Theory]
    [InlineData("whatdo-a1b2c3d4", "whatdo/status/whatdo/a1b2c3d4")]
    [InlineData("my-cool-repo-a1b2c3d4", "whatdo/status/my-cool-repo/a1b2c3d4")]
    public void ForInstance_SplitsOnLastDash(string instanceId, string expected)
    {
        var topic = MqttTopics.ForInstance(instanceId);

        Assert.Equal(expected, topic);
    }

    [Fact]
    public void ForInstance_WithoutDash_ReturnsSingleSegmentTopic()
    {
        var topic = MqttTopics.ForInstance("instance");

        Assert.Equal("whatdo/status/instance", topic);
    }
}
