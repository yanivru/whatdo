namespace WhatDo.StatusService.Status;

public static class HookEventStatusExtensions
{
    public static string ToWireString(this HookEventStatus status) => status switch
    {
        HookEventStatus.Idle => "idle",
        HookEventStatus.Working => "working",
        HookEventStatus.PendingInput => "pending_input",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
