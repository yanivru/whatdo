using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhatDo.StatusService.Status;

namespace WhatDo.StatusService.Mqtt;

/// <summary>
/// Periodically removes instances that haven't sent any hook event in
/// StaleThreshold (treated as crashed — e.g. CLI killed without SessionEnd firing).
/// Removal goes through the same IInstanceStore.Remove + IMqttPublisher.ClearInstanceStatusAsync
/// path as an explicit SessionEnd, so there's a single removal code path, not two.
/// </summary>
public sealed class StaleInstanceSweepService(
    IInstanceStore store,
    IMqttPublisher mqttPublisher,
    ILogger<StaleInstanceSweepService> logger) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(3);

    public static IReadOnlyList<string> FindStaleInstanceIds(
        IReadOnlyCollection<InstanceStatusInfo> instances,
        DateTimeOffset now,
        TimeSpan staleThreshold)
        => instances
            .Where(i => now - i.LastSeenUtc > staleThreshold)
            .Select(i => i.InstanceId)
            .ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var staleIds = FindStaleInstanceIds(store.GetAll(), DateTimeOffset.UtcNow, StaleThreshold);

                foreach (var instanceId in staleIds)
                {
                    logger.LogInformation(
                        "Removing stale instance {InstanceId} (no event for {Threshold})",
                        instanceId,
                        StaleThreshold);
                    store.Remove(instanceId);
                    await mqttPublisher.ClearInstanceStatusAsync(instanceId, stoppingToken);
                }

                if (staleIds.Count > 0)
                {
                    var aggregate = AggregateCalculator.Calculate(store.GetAll());
                    await mqttPublisher.PublishAggregateAsync(
                        new AggregateStatusMessage(
                            aggregate.Status.ToWireString(),
                            aggregate.WorkingCount,
                            aggregate.IdleCount,
                            aggregate.PendingCount,
                            aggregate.Total),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
