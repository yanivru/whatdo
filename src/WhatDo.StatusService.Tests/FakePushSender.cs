using WhatDo.StatusService.Push;

namespace WhatDo.StatusService.Tests;

public sealed class FakePushSender : IPushSender
{
    public List<(PushSubscriptionInfo Subscription, string Payload)> Sent { get; } = new();
    public Func<PushSubscriptionInfo, PushSendResult> ResultFor { get; set; } = _ => PushSendResult.Sent;
    public bool Throw { get; set; }

    public Task<PushSendResult> SendAsync(PushSubscriptionInfo subscription, string payload, CancellationToken cancellationToken = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("boom");
        }

        Sent.Add((subscription, payload));
        return Task.FromResult(ResultFor(subscription));
    }
}
