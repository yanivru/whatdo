using System.Diagnostics.CodeAnalysis;

namespace WhatDo.StatusService.Status;

public interface IInstanceStore
{
    void Upsert(string instanceId, string name, string cwd, HookEventStatus status, string lastEvent);

    void Remove(string instanceId);

    bool TryGet(string instanceId, [NotNullWhen(true)] out InstanceStatusInfo? info);

    IReadOnlyList<InstanceStatusInfo> GetAll();
}
