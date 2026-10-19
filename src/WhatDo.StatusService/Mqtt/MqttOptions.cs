namespace WhatDo.StatusService.Mqtt;

public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 8883;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ClientIdPrefix { get; set; } = "whatdo-status-service";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
