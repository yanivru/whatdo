using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WhatDo.StatusService.Push;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class PushNotifierTests
{
    private readonly InMemoryPushSubscriptionStore _store = new();
    private readonly FakePushSender _sender = new();
    private readonly PushNotifier _notifier;

    public PushNotifierTests()
    {
        _notifier = new PushNotifier(_store, _sender, NullLogger<PushNotifier>.Instance);
    }

    [Fact]
    public async Task Notify_SendsPayloadToEverySubscription()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/1", "k", "a"));
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/2", "k", "a"));

        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        Assert.Equal(2, _sender.Sent.Count);
        using var doc = JsonDocument.Parse(_sender.Sent[0].Payload);
        Assert.Equal("Copilot needs input", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("whatdo is waiting for you", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal("whatdo-a1b2c3d4", doc.RootElement.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task Notify_NoSubscriptions_DoesNothing()
    {
        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Notify_ExpiredSubscription_IsRemoved()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/gone", "k", "a"));
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/ok", "k", "a"));
        _sender.ResultFor = s => s.Endpoint.EndsWith("gone") ? PushSendResult.Expired : PushSendResult.Sent;

        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        var remaining = Assert.Single(_store.GetAll());
        Assert.Equal("https://push.example/ok", remaining.Endpoint);
    }

    [Fact]
    public async Task Notify_FailedSend_KeepsSubscription()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/1", "k", "a"));
        _sender.ResultFor = _ => PushSendResult.Failed;

        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        Assert.Single(_store.GetAll());
    }

    [Fact]
    public async Task Notify_SenderThrows_DoesNotPropagate()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/1", "k", "a"));
        _sender.Throw = true;

        var exception = await Record.ExceptionAsync(() => _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo"));

        Assert.Null(exception);
    }
}
