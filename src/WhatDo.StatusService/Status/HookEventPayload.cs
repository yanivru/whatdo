using System.Text.Json.Serialization;

namespace WhatDo.StatusService.Status;

/// <summary>
/// Represents the JSON body Copilot CLI POSTs to a hook's HTTP endpoint,
/// using the VS Code-compatible (PascalCase event name, snake_case fields) format.
/// The Notification event is a documented exception: it always sends "sessionId"
/// in camelCase even though every other field name follows the snake_case format,
/// so both spellings are accepted and merged into SessionId.
/// </summary>
public sealed record HookEventPayload
{
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionIdSnakeCase { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionIdCamelCase { get; init; }

    [JsonIgnore]
    public string? SessionId => SessionIdSnakeCase ?? SessionIdCamelCase;

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; init; }
}
