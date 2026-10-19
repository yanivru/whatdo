using MQTTnet;

namespace WhatDo.StatusService.Mqtt;

/// <summary>
/// Exposes the current MQTT client to publishers without giving them ownership of its
/// connection lifecycle (that belongs to MqttConnectionService, implemented later).
/// </summary>
public interface IManagedMqttClientAccessor
{
    IMqttClient? Client { get; }
}
