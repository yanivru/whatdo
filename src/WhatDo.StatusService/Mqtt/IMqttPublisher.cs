namespace WhatDo.StatusService.Mqtt;

/// <summary>
/// Publishes retained status messages to the MQTT broker. Implementations must never
/// throw out to callers — publish failures are logged internally and self-correct on
/// the next event or sweep pass (see design spec's Error Handling section).
/// </summary>
public interface IMqttPublisher
{
    Task PublishInstanceStatusAsync(string instanceId, InstanceStatusMessage message, CancellationToken cancellationToken = default);

    /// <summary>Clears the retained message for a removed instance (empty payload, retain=true).</summary>
    Task ClearInstanceStatusAsync(string instanceId, CancellationToken cancellationToken = default);

    Task PublishAggregateAsync(AggregateStatusMessage message, CancellationToken cancellationToken = default);
}
