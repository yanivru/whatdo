using System.Text.Json;
using WhatDo.StatusService.Mqtt;
using WhatDo.StatusService.Push;
using WhatDo.StatusService.Status;

namespace WhatDo.StatusService.Endpoints;

public static class StatusEventEndpoints
{
    public const string SecretHeaderName = "X-Status-Secret";

    public static void MapStatusEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/status-event", async (HttpRequest request, IInstanceStore store, IMqttPublisher mqttPublisher, IPushNotifier pushNotifier, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("StatusEventEndpoint");

            var expectedSecret = config["StatusIngest:Secret"];
            if (!string.IsNullOrEmpty(expectedSecret))
            {
                var providedSecret = request.Headers[SecretHeaderName].ToString();
                if (!string.Equals(providedSecret, expectedSecret, StringComparison.Ordinal))
                {
                    logger.LogWarning("Rejected status event: bad or missing {Header} header", SecretHeaderName);
                    return Results.Unauthorized();
                }
            }

            HookEventPayload? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<HookEventPayload>(request.Body);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Ignored malformed status event payload");
                return Results.Ok(); // malformed payload: log-and-ignore per design, never fail the CLI hook
            }

            if (payload is null)
            {
                logger.LogWarning("Ignored empty status event payload");
                return Results.Ok();
            }

            var instanceId = InstanceIdFactory.CreateInstanceId(payload.Cwd, payload.SessionId);
            var name = InstanceIdFactory.CreateName(payload.Cwd);
            var result = StatusMapper.Map(payload);

            logger.LogInformation(
                "Status event received: HookEventName={HookEventName} NotificationType={NotificationType} SessionId={SessionId} Cwd={Cwd} -> Action={Action} Status={Status}",
                payload.HookEventName, payload.NotificationType, payload.SessionId, payload.Cwd, result.Action, result.Status);

            switch (result.Action)
            {
                case StatusAction.UpdateStatus:
                    var cwd = payload.Cwd ?? "";
                    var lastEvent = payload.HookEventName ?? "";
                    var wasPendingInput = store.TryGet(instanceId, out var previous) &&
                                          previous.Status == HookEventStatus.PendingInput;
                    store.Upsert(instanceId, name, cwd, result.Status!.Value, lastEvent);
                    // Publish from the values just written rather than re-reading the store: a
                    // concurrent remove (e.g. a racing SessionEnd or the stale-instance sweep)
                    // could otherwise make a GetAll().First(...) lookup throw here.
                    await mqttPublisher.PublishInstanceStatusAsync(instanceId, new InstanceStatusMessage(
                        result.Status.Value.ToWireString(),
                        name,
                        cwd,
                        lastEvent,
                        DateTimeOffset.UtcNow));

                    if (result.Status.Value == HookEventStatus.PendingInput && !wasPendingInput)
                    {
                        await pushNotifier.NotifyPendingInputAsync(instanceId, name);
                    }
                    break;
                case StatusAction.RemoveInstance:
                    store.Remove(instanceId);
                    await mqttPublisher.ClearInstanceStatusAsync(instanceId);
                    break;
                case StatusAction.Ignore:
                default:
                    break;
            }

            if (result.Action is StatusAction.UpdateStatus or StatusAction.RemoveInstance)
            {
                var aggregate = AggregateCalculator.Calculate(store.GetAll());
                await mqttPublisher.PublishAggregateAsync(new AggregateStatusMessage(
                    aggregate.Status.ToWireString(),
                    aggregate.WorkingCount,
                    aggregate.IdleCount,
                    aggregate.PendingCount,
                    aggregate.Total));
            }

            return Results.Ok();
        });

        app.MapGet("/api/status", (IInstanceStore store) =>
        {
            var instances = store.GetAll()
                .OrderBy(i => i.Name)
                .Select(i => new
                {
                    i.Name,
                    Status = i.Status.ToString(),
                    i.LastEvent,
                    LastSeenUtc = i.LastSeenUtc.ToString("HH:mm:ss")
                });
            return Results.Ok(instances);
        });
    }
}
