namespace WhatDo.StatusService.Status;

/// <summary>
/// Pure function: given the current instance list, computes the aggregate status
/// using a worst-case-wins rule (PendingInput > Working > Idle).
/// </summary>
public static class AggregateCalculator
{
    public static AggregateStatusInfo Calculate(IReadOnlyCollection<InstanceStatusInfo> instances)
    {
        var workingCount = instances.Count(i => i.Status == HookEventStatus.Working);
        var idleCount = instances.Count(i => i.Status == HookEventStatus.Idle);
        var pendingCount = instances.Count(i => i.Status == HookEventStatus.PendingInput);

        var status = pendingCount > 0
            ? HookEventStatus.PendingInput
            : workingCount > 0
                ? HookEventStatus.Working
                : HookEventStatus.Idle;

        return new AggregateStatusInfo(status, workingCount, idleCount, pendingCount, instances.Count);
    }
}
