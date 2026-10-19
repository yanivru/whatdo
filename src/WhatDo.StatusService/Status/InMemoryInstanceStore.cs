using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace WhatDo.StatusService.Status;

public sealed class InMemoryInstanceStore : IInstanceStore
{
    private readonly ConcurrentDictionary<string, InstanceStatusInfo> _instances = new();

    public void Upsert(string instanceId, string name, string cwd, HookEventStatus status, string lastEvent)
    {
        _instances[instanceId] = new InstanceStatusInfo(
            instanceId,
            name,
            cwd,
            status,
            lastEvent,
            DateTimeOffset.UtcNow);
    }

    public void Remove(string instanceId)
    {
        _instances.TryRemove(instanceId, out _);
    }

    public bool TryGet(string instanceId, [NotNullWhen(true)] out InstanceStatusInfo? info)
        => _instances.TryGetValue(instanceId, out info);

    public IReadOnlyList<InstanceStatusInfo> GetAll()
        => _instances.Values.ToList();
}
