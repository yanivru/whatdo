using System.Text.Json.Serialization;

namespace WhatDo.StatusService.Mqtt;

public sealed record InstanceStatusMessage(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("lastEvent")] string LastEvent,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);
