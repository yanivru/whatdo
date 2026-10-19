using System.Text.Json;

namespace WhatDo.StatusService.Push;

public sealed class PushNotifier : IPushNotifier
{
    private readonly IPushSubscriptionStore _store;
    private readonly IPushSender _sender;
    private readonly ILogger<PushNotifier> _logger;

    public PushNotifier(IPushSubscriptionStore store, IPushSender sender, ILogger<PushNotifier> logger)
    {
        _store = store;
        _sender = sender;
        _logger = logger;
    }

    public async Task NotifyPendingInputAsync(string instanceId, string name)
    {
        var subscriptions = _store.GetAll();
        if (subscriptions.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = "Copilot needs input",
            body = $"{name} is waiting for you",
            tag = instanceId
        });

        foreach (var subscription in subscriptions)
        {
            try
            {
                var result = await _sender.SendAsync(subscription, payload);
                if (result == PushSendResult.Expired)
                {
                    _store.Remove(subscription.Endpoint);
                }
                else if (result == PushSendResult.Failed)
                {
                    _logger.LogWarning("Push send failed for subscription {Endpoint}", subscription.Endpoint);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Push send threw for subscription {Endpoint}", subscription.Endpoint);
            }
        }
    }
}
