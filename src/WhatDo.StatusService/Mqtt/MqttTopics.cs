namespace WhatDo.StatusService.Mqtt;

public static class MqttTopics
{
    public const string AggregateTopic = "whatdo/status/_aggregate";

    /// <summary>
    /// Per-instance topic: whatdo/status/&lt;folder&gt;/&lt;shortSessionId&gt;.
    /// instanceId is already "&lt;folder&gt;-&lt;shortSessionId&gt;" (see InstanceIdFactory),
    /// so split on the last '-' to recover the two path segments.
    /// </summary>
    public static string ForInstance(string instanceId)
    {
        var lastDash = instanceId.LastIndexOf('-');
        if (lastDash < 0)
        {
            return $"whatdo/status/{instanceId}";
        }

        var folder = instanceId[..lastDash];
        var shortSessionId = instanceId[(lastDash + 1)..];
        return $"whatdo/status/{folder}/{shortSessionId}";
    }
}
