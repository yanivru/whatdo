using System.Collections.Concurrent;
using WhatDo.StatusService.Mqtt;

namespace WhatDo.StatusService.Tests;

public sealed class FakeMqttPublisher : IMqttPublisher
{
    public ConcurrentDictionary<string, InstanceStatusMessage> PublishedInstances { get; } = new();
    public ConcurrentBag<string> ClearedInstanceIds { get; } = new();
    public ConcurrentQueue<AggregateStatusMessage> PublishedAggregates { get; } = new();

    public Task PublishInstanceStatusAsync(string instanceId, InstanceStatusMessage message, CancellationToken cancellationToken = default)
    {
        PublishedInstances[instanceId] = message;
        return Task.CompletedTask;
    }

    public Task ClearInstanceStatusAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        PublishedInstances.TryRemove(instanceId, out _);
        ClearedInstanceIds.Add(instanceId);
        return Task.CompletedTask;
    }

    public Task PublishAggregateAsync(AggregateStatusMessage message, CancellationToken cancellationToken = default)
    {
        PublishedAggregates.Enqueue(message);
        return Task.CompletedTask;
    }
}
