namespace WhatDo.StatusService.Push;

public interface IPushNotifier
{
    Task NotifyPendingInputAsync(string instanceId, string name);
}
