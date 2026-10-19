using System.Collections.Concurrent;

namespace WhatDo.StatusService.Push;

public sealed class InMemoryPushSubscriptionStore : IPushSubscriptionStore
{
    private readonly ConcurrentDictionary<string, PushSubscriptionInfo> _subscriptions = new();
    private readonly object _addLock = new();
    private readonly int _maxSubscriptions;

    public InMemoryPushSubscriptionStore(int maxSubscriptions = 50)
    {
        _maxSubscriptions = maxSubscriptions;
    }

    public bool TryAdd(PushSubscriptionInfo subscription)
    {
        lock (_addLock)
        {
            if (!_subscriptions.ContainsKey(subscription.Endpoint) && _subscriptions.Count >= _maxSubscriptions)
            {
                return false;
            }

            _subscriptions[subscription.Endpoint] = subscription;
            return true;
        }
    }

    public void Remove(string endpoint)
    {
        _subscriptions.TryRemove(endpoint, out _);
    }

    public IReadOnlyList<PushSubscriptionInfo> GetAll()
        => _subscriptions.Values.ToList();
}
