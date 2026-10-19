namespace WhatDo.StatusService.Push;

public interface IPushSubscriptionStore
{
    /// <summary>Adds or refreshes a subscription. Returns false when the cap is reached for a new endpoint.</summary>
    bool TryAdd(PushSubscriptionInfo subscription);

    void Remove(string endpoint);

    IReadOnlyList<PushSubscriptionInfo> GetAll();
}
