using System.Net;
using Microsoft.Extensions.Options;
using WebPush;

namespace WhatDo.StatusService.Push;

public sealed class WebPushSender : IPushSender
{
    private readonly WebPushClient _client = new();
    private readonly Lazy<VapidDetails> _vapid;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(IOptions<PushOptions> options, ILogger<WebPushSender> logger)
    {
        var o = options.Value;
        _vapid = new Lazy<VapidDetails>(() => new VapidDetails(o.Subject, o.PublicKey, o.PrivateKey));
        _logger = logger;
    }

    public async Task<PushSendResult> SendAsync(PushSubscriptionInfo subscription, string payload, CancellationToken cancellationToken = default)
    {
        try
        {
            var target = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
            await _client.SendNotificationAsync(target, payload, _vapid.Value, cancellationToken);
            return PushSendResult.Sent;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushSendResult.Expired;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web Push delivery failed");
            return PushSendResult.Failed;
        }
    }
}
