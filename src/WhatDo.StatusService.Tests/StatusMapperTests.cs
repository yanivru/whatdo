using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class StatusMapperTests
{
    [Theory]
    [InlineData("SessionStart", HookEventStatus.Idle)]
    [InlineData("UserPromptSubmit", HookEventStatus.Working)]
    [InlineData("PreToolUse", HookEventStatus.Working)]
    [InlineData("PostToolUse", HookEventStatus.Working)]
    [InlineData("PostToolUseFailure", HookEventStatus.Working)]
    [InlineData("Stop", HookEventStatus.Idle)]
    public void Map_SimpleEvents_ReturnsExpectedStatus(string hookEventName, HookEventStatus expected)
    {
        var payload = new HookEventPayload { HookEventName = hookEventName };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.UpdateStatus, result.Action);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Map_SessionEnd_ReturnsRemoveInstance()
    {
        var payload = new HookEventPayload { HookEventName = "SessionEnd" };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.RemoveInstance, result.Action);
        Assert.Null(result.Status);
    }

    [Theory]
    [InlineData("permission_prompt", HookEventStatus.PendingInput)]
    [InlineData("elicitation_dialog", HookEventStatus.PendingInput)]
    [InlineData("agent_idle", HookEventStatus.Idle)]
    [InlineData("shell_completed", HookEventStatus.Idle)]
    [InlineData("agent_completed", HookEventStatus.Idle)]
    [InlineData("shell_detached_completed", HookEventStatus.Idle)]
    public void Map_Notification_ReturnsExpectedStatus(string notificationType, HookEventStatus expected)
    {
        var payload = new HookEventPayload { HookEventName = "Notification", NotificationType = notificationType };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.UpdateStatus, result.Action);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Map_UnknownNotificationType_ReturnsIgnore()
    {
        var payload = new HookEventPayload { HookEventName = "Notification", NotificationType = "something_new" };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.Ignore, result.Action);
    }

    [Fact]
    public void Map_UnknownEventName_ReturnsIgnore()
    {
        var payload = new HookEventPayload { HookEventName = "SomethingUnexpected" };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.Ignore, result.Action);
    }
}
