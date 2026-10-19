using WhatDo.StatusService.Push;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class InMemoryPushSubscriptionStoreTests
{
    private static PushSubscriptionInfo Sub(string endpoint, string auth = "auth")
        => new(endpoint, "p256dh", auth);

    [Fact]
    public void TryAdd_NewSubscription_AppearsInGetAll()
    {
        var store = new InMemoryPushSubscriptionStore();

        Assert.True(store.TryAdd(Sub("https://push.example/1")));

        Assert.Single(store.GetAll());
    }

    [Fact]
    public void TryAdd_SameEndpoint_ReplacesInsteadOfDuplicating()
    {
        var store = new InMemoryPushSubscriptionStore();
        store.TryAdd(Sub("https://push.example/1", "old"));

        store.TryAdd(Sub("https://push.example/1", "new"));

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("new", all[0].Auth);
    }

    [Fact]
    public void TryAdd_AtCap_RejectsNewButAllowsRefreshOfExisting()
    {
        var store = new InMemoryPushSubscriptionStore(maxSubscriptions: 2);
        store.TryAdd(Sub("https://push.example/1"));
        store.TryAdd(Sub("https://push.example/2"));

        Assert.False(store.TryAdd(Sub("https://push.example/3")));
        Assert.True(store.TryAdd(Sub("https://push.example/1", "refreshed")));
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void Remove_UnknownEndpoint_DoesNotThrow()
    {
        var store = new InMemoryPushSubscriptionStore();

        Assert.Null(Record.Exception(() => store.Remove("https://push.example/none")));
    }

    [Fact]
    public void Remove_ExistingEndpoint_RemovesIt()
    {
        var store = new InMemoryPushSubscriptionStore();
        store.TryAdd(Sub("https://push.example/1"));

        store.Remove("https://push.example/1");

        Assert.Empty(store.GetAll());
    }
}
