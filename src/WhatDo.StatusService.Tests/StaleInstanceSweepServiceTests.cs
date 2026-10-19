using WhatDo.StatusService.Mqtt;
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class StaleInstanceSweepServiceTests
{
    [Fact]
    public void FindStaleInstanceIds_InstanceOlderThanThreshold_IsReturned()
    {
        var now = DateTimeOffset.UtcNow;
        var instances = new[]
        {
            new InstanceStatusInfo("fresh", "fresh", "/a", HookEventStatus.Idle, "Stop", now.AddSeconds(-30)),
            new InstanceStatusInfo("stale", "stale", "/b", HookEventStatus.Working, "PostToolUse", now.AddMinutes(-5)),
        };

        var staleIds = StaleInstanceSweepService.FindStaleInstanceIds(instances, now, TimeSpan.FromMinutes(3));

        Assert.Equal(new[] { "stale" }, staleIds);
    }

    [Fact]
    public void FindStaleInstanceIds_NoneStale_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var instances = new[]
        {
            new InstanceStatusInfo("fresh", "fresh", "/a", HookEventStatus.Idle, "Stop", now.AddSeconds(-10)),
        };

        var staleIds = StaleInstanceSweepService.FindStaleInstanceIds(instances, now, TimeSpan.FromMinutes(3));

        Assert.Empty(staleIds);
    }
}
