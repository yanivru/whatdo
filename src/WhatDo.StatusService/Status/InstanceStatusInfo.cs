namespace WhatDo.StatusService.Status;

public sealed record InstanceStatusInfo(
    string InstanceId,
    string Name,
    string Cwd,
    HookEventStatus Status,
    string LastEvent,
    DateTimeOffset LastSeenUtc);
