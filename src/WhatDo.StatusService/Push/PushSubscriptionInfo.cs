namespace WhatDo.StatusService.Push;

public sealed record PushSubscriptionInfo(string Endpoint, string P256dh, string Auth);
