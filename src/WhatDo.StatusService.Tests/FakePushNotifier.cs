using WhatDo.StatusService.Push;

namespace WhatDo.StatusService.Tests;

public sealed class FakePushNotifier : IPushNotifier
{
    public List<(string InstanceId, string Name)> Notified { get; } = new();

    public Task NotifyPendingInputAsync(string instanceId, string name)
    {
        Notified.Add((instanceId, name));
        return Task.CompletedTask;
    }
}
