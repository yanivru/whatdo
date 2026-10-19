namespace WhatDo.StatusService.Status;

public sealed record AggregateStatusInfo(
    HookEventStatus Status,
    int WorkingCount,
    int IdleCount,
    int PendingCount,
    int Total);
