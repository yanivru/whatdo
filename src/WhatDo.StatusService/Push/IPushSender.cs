namespace WhatDo.StatusService.Push;

public enum PushSendResult
{
    Sent,
    Expired,
    Failed
}

public interface IPushSender
{
    Task<PushSendResult> SendAsync(PushSubscriptionInfo subscription, string payload, CancellationToken cancellationToken = default);
}
