using System.Text.Json.Serialization;

namespace WhatDo.StatusService.Mqtt;

public sealed record AggregateStatusMessage(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("workingCount")] int WorkingCount,
    [property: JsonPropertyName("idleCount")] int IdleCount,
    [property: JsonPropertyName("pendingCount")] int PendingCount,
    [property: JsonPropertyName("total")] int Total);
