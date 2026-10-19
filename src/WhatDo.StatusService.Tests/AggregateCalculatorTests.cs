using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class AggregateCalculatorTests
{
    [Fact]
    public void Calculate_EmptyList_ReturnsIdleWithZeroCounts()
    {
        var result = AggregateCalculator.Calculate(Array.Empty<InstanceStatusInfo>());

        Assert.Equal(HookEventStatus.Idle, result.Status);
        Assert.Equal(0, result.WorkingCount);
        Assert.Equal(0, result.IdleCount);
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void Calculate_AllIdle_ReturnsIdle()
    {
        var instances = new[]
        {
            new InstanceStatusInfo("a", "a", "/a", HookEventStatus.Idle, "Stop", DateTimeOffset.UtcNow),
            new InstanceStatusInfo("b", "b", "/b", HookEventStatus.Idle, "Stop", DateTimeOffset.UtcNow),
        };

        var result = AggregateCalculator.Calculate(instances);

        Assert.Equal(HookEventStatus.Idle, result.Status);
        Assert.Equal(2, result.IdleCount);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void Calculate_OneWorkingRestIdle_ReturnsWorking()
    {
        var instances = new[]
        {
            new InstanceStatusInfo("a", "a", "/a", HookEventStatus.Idle, "Stop", DateTimeOffset.UtcNow),
            new InstanceStatusInfo("b", "b", "/b", HookEventStatus.Working, "UserPromptSubmit", DateTimeOffset.UtcNow),
        };

        var result = AggregateCalculator.Calculate(instances);

        Assert.Equal(HookEventStatus.Working, result.Status);
        Assert.Equal(1, result.WorkingCount);
        Assert.Equal(1, result.IdleCount);
    }

    [Fact]
    public void Calculate_OnePendingRestWorking_ReturnsPendingInput()
    {
        var instances = new[]
        {
            new InstanceStatusInfo("a", "a", "/a", HookEventStatus.Working, "PostToolUse", DateTimeOffset.UtcNow),
            new InstanceStatusInfo("b", "b", "/b", HookEventStatus.PendingInput, "Notification", DateTimeOffset.UtcNow),
        };

        var result = AggregateCalculator.Calculate(instances);

        Assert.Equal(HookEventStatus.PendingInput, result.Status);
        Assert.Equal(1, result.PendingCount);
        Assert.Equal(2, result.Total);
    }
}
