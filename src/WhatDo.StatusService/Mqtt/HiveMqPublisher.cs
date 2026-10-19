using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace WhatDo.StatusService.Mqtt;

/// <summary>
/// Publishes via the shared MQTT client owned by MqttConnectionService.
/// If MQTT isn't configured (no Host set, e.g. local dev without HiveMQ credentials),
/// every publish is a silent no-op — the in-memory store and HTTP dashboard still work
/// without MQTT, by design.
/// </summary>
public sealed class HiveMqPublisher(
    IManagedMqttClientAccessor clientAccessor,
    IOptions<MqttOptions> options,
    ILogger<HiveMqPublisher> logger) : IMqttPublisher
{
    private readonly MqttOptions _options = options.Value;

    public Task PublishInstanceStatusAsync(string instanceId, InstanceStatusMessage message, CancellationToken cancellationToken = default)
        => PublishAsync(MqttTopics.ForInstance(instanceId), JsonSerializer.Serialize(message), cancellationToken);

    public Task ClearInstanceStatusAsync(string instanceId, CancellationToken cancellationToken = default)
        => PublishAsync(MqttTopics.ForInstance(instanceId), string.Empty, cancellationToken);

    public Task PublishAggregateAsync(AggregateStatusMessage message, CancellationToken cancellationToken = default)
        => PublishAsync(MqttTopics.AggregateTopic, JsonSerializer.Serialize(message), cancellationToken);

    private async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return;
        }

        var client = clientAccessor.Client;
        if (client is null)
        {
            logger.LogDebug("Skipped MQTT publish to {Topic}: client not yet available", topic);
            return;
        }

        try
        {
            var appMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(true)
                .Build();

            await client.PublishAsync(appMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish MQTT message to {Topic}", topic);
        }
    }
}
