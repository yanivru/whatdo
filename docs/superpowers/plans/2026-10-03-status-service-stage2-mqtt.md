# WhatDo Status Service — Stage 2: MQTT via HiveMQ Cloud Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Republish every instance status change over **MQTT** (HiveMQ Cloud Free tier) as a **retained** message, so any number of poll-free clients (web dashboard, future ESP32, future Android) can subscribe and get instant updates — including the current status immediately on connect/reboot, with no REST polling required. Add a periodic staleness sweep so instances that crash without firing `SessionEnd` eventually disappear. Azure Table Storage persistence is explicitly **out of scope** for this stage (Stage 1's in-memory store is kept as-is); this stage only adds the MQTT republish path and the staleness sweep on top of it.

**Architecture:** `StatusEventEndpoints` (existing ingest endpoint) keeps writing to `IInstanceStore` exactly as it does today, and additionally calls a new `IMqttPublisher` abstraction after every upsert/remove to publish a retained message — fire-and-forget, never blocking or failing the HTTP response. A new `BackgroundService` (`MqttConnectionService`) owns the single persistent MQTTnet client connection to HiveMQ Cloud (auto-reconnect with backoff is built into MQTTnet's `ManagedMqttClient`). A second `BackgroundService` (`StaleInstanceSweepService`) runs every 60s, finds instances with no event in ~3 minutes, and removes them from the store (which in turn publishes an empty retained message, clearing that topic) exactly like a `SessionEnd` would.

**Tech Stack:** .NET 8, **MQTTnet v5** (`MqttClientFactory`, `ManagedMqttClient` for auto-reconnect), HiveMQ Cloud (Free tier: 100 connections, 10GB/month, TLS on port 8883, username+password — no cert provisioning needed), xUnit with a fake `IMqttPublisher` test double for the endpoint/sweep unit tests.

---

## Reference: Topics and Payload Shapes (from design spec)

- Per instance (retained): `whatdo/status/<folder>/<shortSessionId>` →
  `{ "status": "working"|"idle"|"pending_input", "name": "...", "cwd": "...", "lastEvent": "...", "updatedAt": "2026-10-03T12:34:56Z" }`
  On removal (`SessionEnd` or staleness sweep): publish an **empty payload with retain=true** to the same topic, which tells the broker to delete the retained message (MQTT spec behavior).
- Aggregate (retained): `whatdo/status/_aggregate` →
  `{ "status": "working"|"idle"|"pending_input", "workingCount": n, "idleCount": n, "pendingCount": n, "total": n }`
  Recomputed and republished after every change (not just on the changed instance), since any single instance's change can change the aggregate. Empty set → `{ "status": "idle", "workingCount": 0, "idleCount": 0, "pendingCount": 0, "total": 0 }`.
- **Aggregation rule** (worst-case wins): `pending_input` > `working` > `idle`.
- **Staleness threshold:** no event for an instance in **3 minutes** → treated as crashed, removed exactly like `SessionEnd`. Sweep runs every **60 seconds**.

---

### Task 1: Add MQTTnet package and HiveMQ Cloud connection settings

**Files:**
- Modify: `src/WhatDo.StatusService/WhatDo.StatusService.csproj`
- Modify: `src/WhatDo.StatusService/appsettings.json`
- Modify: `src/WhatDo.StatusService/appsettings.Development.json`

- [ ] **Step 1: Add the MQTTnet package**

```powershell
dotnet add src/WhatDo.StatusService/WhatDo.StatusService.csproj package MQTTnet
```

Expected: `info : PackageReference for package 'MQTTnet' ... added to file ...` (resolves to v5.x).

- [ ] **Step 2: Add HiveMQ connection settings placeholders to `appsettings.json`**

Add a new `Mqtt` section (values filled in at deploy time via environment-specific app settings, same pattern as `StatusIngest:Secret`):

```json
"Mqtt": {
  "Host": "",
  "Port": 8883,
  "Username": "",
  "Password": "",
  "ClientIdPrefix": "whatdo-status-service"
}
```

- [ ] **Step 3: Add a local dev placeholder to `appsettings.Development.json`**

Same `Mqtt` section, with a comment (via a sibling `_comment` string field, since JSON has no comments) noting these must be replaced with real HiveMQ Cloud Free-tier cluster credentials for local MQTT testing, or left blank to skip MQTT locally (see Task 3's connect-failure handling).

- [ ] **Step 4: Build to verify it compiles**

```powershell
dotnet build WhatDo.sln
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "Add MQTTnet package and HiveMQ connection settings"
```

---

### Task 2: AggregateCalculator (pure function, TDD)

**Files:**
- Create: `src/WhatDo.StatusService/Status/AggregateStatusInfo.cs`
- Create: `src/WhatDo.StatusService/Status/AggregateCalculator.cs`
- Test: `src/WhatDo.StatusService.Tests/AggregateCalculatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/WhatDo.StatusService.Tests/AggregateCalculatorTests.cs`:

```csharp
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class AggregateCalculatorTests
{
    [Fact]
    public void Calculate_EmptyList_ReturnsIdleWithZeroCounts()
    {
        var result = AggregateCalculator.Calculate(Array.Empty<InstanceStatusInfo>());

        Assert.Equal(HookEventStatus.Idle, result.Status);
        Assert.Equal(0, result.WorkingCount);
        Assert.Equal(0, result.IdleCount);
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void Calculate_AllIdle_ReturnsIdle()
    {
        var instances = new[]
        {
            new InstanceStatusInfo("a", "a", "/a", HookEventStatus.Idle, "Stop", DateTimeOffset.UtcNow),
            new InstanceStatusInfo("b", "b", "/b", HookEventStatus.Idle, "Stop", DateTimeOffset.UtcNow),
        };

        var result = AggregateCalculator.Calculate(instances);

        Assert.Equal(HookEventStatus.Idle, result.Status);
        Assert.Equal(2, result.IdleCount);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void Calculate_OneWorkingRestIdle_ReturnsWorking()
    {
        var instances = new[]
        {
            new InstanceStatusInfo("a", "a", "/a", HookEventStatus.Idle, "Stop", DateTimeOffset.UtcNow),
            new InstanceStatusInfo("b", "b", "/b", HookEventStatus.Working, "UserPromptSubmit", DateTimeOffset.UtcNow),
        };

        var result = AggregateCalculator.Calculate(instances);

        Assert.Equal(HookEventStatus.Working, result.Status);
        Assert.Equal(1, result.WorkingCount);
        Assert.Equal(1, result.IdleCount);
    }

    [Fact]
    public void Calculate_OnePendingRestWorking_ReturnsPendingInput()
    {
        var instances = new[]
        {
            new InstanceStatusInfo("a", "a", "/a", HookEventStatus.Working, "PostToolUse", DateTimeOffset.UtcNow),
            new InstanceStatusInfo("b", "b", "/b", HookEventStatus.PendingInput, "Notification", DateTimeOffset.UtcNow),
        };

        var result = AggregateCalculator.Calculate(instances);

        Assert.Equal(HookEventStatus.PendingInput, result.Status);
        Assert.Equal(1, result.PendingCount);
        Assert.Equal(2, result.Total);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: build FAILS — `AggregateCalculator`/`AggregateStatusInfo` do not exist.

- [ ] **Step 3: Create the aggregate result record**

Create `src/WhatDo.StatusService/Status/AggregateStatusInfo.cs`:

```csharp
namespace WhatDo.StatusService.Status;

public sealed record AggregateStatusInfo(
    HookEventStatus Status,
    int WorkingCount,
    int IdleCount,
    int PendingCount,
    int Total);
```

- [ ] **Step 4: Create the AggregateCalculator**

Create `src/WhatDo.StatusService/Status/AggregateCalculator.cs`:

```csharp
namespace WhatDo.StatusService.Status;

/// <summary>
/// Pure function: given the current instance list, computes the aggregate status
/// using a worst-case-wins rule (PendingInput > Working > Idle).
/// </summary>
public static class AggregateCalculator
{
    public static AggregateStatusInfo Calculate(IReadOnlyCollection<InstanceStatusInfo> instances)
    {
        var workingCount = instances.Count(i => i.Status == HookEventStatus.Working);
        var idleCount = instances.Count(i => i.Status == HookEventStatus.Idle);
        var pendingCount = instances.Count(i => i.Status == HookEventStatus.PendingInput);

        var status = pendingCount > 0
            ? HookEventStatus.PendingInput
            : workingCount > 0
                ? HookEventStatus.Working
                : HookEventStatus.Idle;

        return new AggregateStatusInfo(status, workingCount, idleCount, pendingCount, instances.Count);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: all tests pass, including the new `AggregateCalculatorTests`.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "Add AggregateCalculator with worst-case-wins status rule"
```

---

### Task 3: IMqttPublisher abstraction + HiveMQ-backed implementation

**Files:**
- Create: `src/WhatDo.StatusService/Mqtt/IMqttPublisher.cs`
- Create: `src/WhatDo.StatusService/Mqtt/MqttTopics.cs`
- Create: `src/WhatDo.StatusService/Mqtt/InstanceStatusMessage.cs`
- Create: `src/WhatDo.StatusService/Mqtt/AggregateStatusMessage.cs`
- Create: `src/WhatDo.StatusService/Mqtt/HiveMqPublisher.cs`
- Create: `src/WhatDo.StatusService/Mqtt/MqttOptions.cs`

- [ ] **Step 1: Create the MQTT options binding class**

Create `src/WhatDo.StatusService/Mqtt/MqttOptions.cs`:

```csharp
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
```

- [ ] **Step 2: Create the topic naming helper**

Create `src/WhatDo.StatusService/Mqtt/MqttTopics.cs`:

```csharp
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
```

- [ ] **Step 2b: Check `InstanceIdFactory` to confirm the instanceId shape assumed above**

Run:

```powershell
Get-Content src/WhatDo.StatusService/Status/InstanceIdFactory.cs
```

Confirm the format is `<folder>-<first8ofsessionId>` (hyphen-joined, no hyphens inside the sanitized folder segment that would make the split ambiguous beyond the intended one). If the folder itself can contain hyphens (it can, after sanitization — e.g. `my-repo-a1b2c3d4`), `LastIndexOf('-')` is still correct since we only need to split off the *last* 8-char session segment, not the folder's internal hyphens. Adjust the implementation above only if this assumption doesn't hold.

- [ ] **Step 3: Create the message DTOs**

Create `src/WhatDo.StatusService/Mqtt/InstanceStatusMessage.cs`:

```csharp
using System.Text.Json.Serialization;

namespace WhatDo.StatusService.Mqtt;

public sealed record InstanceStatusMessage(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("lastEvent")] string LastEvent,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);
```

Create `src/WhatDo.StatusService/Mqtt/AggregateStatusMessage.cs`:

```csharp
using System.Text.Json.Serialization;

namespace WhatDo.StatusService.Mqtt;

public sealed record AggregateStatusMessage(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("workingCount")] int WorkingCount,
    [property: JsonPropertyName("idleCount")] int IdleCount,
    [property: JsonPropertyName("pendingCount")] int PendingCount,
    [property: JsonPropertyName("total")] int Total);
```

- [ ] **Step 4: Create the IMqttPublisher abstraction**

Create `src/WhatDo.StatusService/Mqtt/IMqttPublisher.cs`:

```csharp
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
```

- [ ] **Step 5: Create the HiveMQ-backed implementation using MQTTnet's ManagedMqttClient**

Create `src/WhatDo.StatusService/Mqtt/HiveMqPublisher.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace WhatDo.StatusService.Mqtt;

/// <summary>
/// Publishes via the shared IManagedMqttClient owned by MqttConnectionService (Task 4).
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
        => PublishAsync(MqttTopics.ForInstance(instanceId), payload: "", cancellationToken);

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
            logger.LogDebug("Skipped MQTT publish to {Topic}: client not yet connected", topic);
            return;
        }

        try
        {
            var appMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(true)
                .Build();

            await client.EnqueueAsync(appMessage);
        }
        catch (Exception ex)
        {
            // Fire-and-forget by design: a publish failure here must never affect the
            // HTTP response to the Copilot CLI hook. The next event or the 60s sweep
            // will naturally retry/self-correct.
            logger.LogWarning(ex, "Failed to publish MQTT message to {Topic}", topic);
        }
    }
}
```

> Note: `IManagedMqttClientAccessor` is defined in Task 4 alongside `MqttConnectionService`, which owns the client's lifetime; `HiveMqPublisher` only reads the current client reference through it, keeping connection management out of the publisher.

- [ ] **Step 6: Build to verify it compiles (expect it won't yet — `IManagedMqttClientAccessor` doesn't exist)**

```powershell
dotnet build WhatDo.sln
```

Expected: build FAILS — `IManagedMqttClientAccessor` not found. This is expected; it's created in Task 4. Do not commit yet — continue to Task 4 first, then come back and build/commit both together.

---

### Task 4: MqttConnectionService (owns the client, auto-reconnect)

> **Post-implementation correction (as-built, 2026-10-03):** MQTTnet v5 removed
> `ManagedMqttClient`/`ManagedMqttClientOptionsBuilder` entirely — it is not available and not
> planned to return (see dotnet/MQTTnet#2162). Every code sketch below that references
> `IManagedMqttClient` is historical/aspirational; the implementation actually uses a plain
> `IMqttClient` plus a hand-rolled, iterative (non-recursive) reconnect loop built on
> `TaskCompletionSource` + the `DisconnectedAsync` event. `IManagedMqttClientAccessor.Client` is
> typed `IMqttClient?`, not `IManagedMqttClient?`. See `MqttConnectionService.cs` for the real
> implementation.

**Files:**
- Create: `src/WhatDo.StatusService/Mqtt/IManagedMqttClientAccessor.cs`
- Create: `src/WhatDo.StatusService/Mqtt/MqttConnectionService.cs`
- Modify: `src/WhatDo.StatusService/Program.cs`

- [ ] **Step 1: Create the accessor interface**

Create `src/WhatDo.StatusService/Mqtt/IManagedMqttClientAccessor.cs`:

```csharp
using MQTTnet;

namespace WhatDo.StatusService.Mqtt;

/// <summary>
/// Exposes the current managed MQTT client to publishers without giving them
/// ownership of its connection lifecycle (that belongs to MqttConnectionService).
/// </summary>
public interface IManagedMqttClientAccessor
{
    IManagedMqttClient? Client { get; }
}
```

- [ ] **Step 2: Create MqttConnectionService**

Create `src/WhatDo.StatusService/Mqtt/MqttConnectionService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace WhatDo.StatusService.Mqtt;

/// <summary>
/// Owns the single long-lived MQTTnet ManagedMqttClient connection to HiveMQ Cloud.
/// ManagedMqttClient handles automatic reconnection with backoff internally, so this
/// service only needs to start it once and stop it on shutdown. If MQTT isn't
/// configured (no Host in settings), this service does nothing — the rest of the app
/// (in-memory store, HTTP dashboard) works fine without it.
/// </summary>
public sealed class MqttConnectionService(
    IOptions<MqttOptions> options,
    ILogger<MqttConnectionService> logger) : BackgroundService, IManagedMqttClientAccessor
{
    private readonly MqttOptions _options = options.Value;
    private IManagedMqttClient? _client;

    public IManagedMqttClient? Client => _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            logger.LogInformation("MQTT not configured (Mqtt:Host is empty) — skipping HiveMQ connection");
            return;
        }

        var clientId = $"{_options.ClientIdPrefix}-{Environment.MachineName}-{Guid.NewGuid():N}"[..Math.Min(48, $"{_options.ClientIdPrefix}-{Environment.MachineName}-{Guid.NewGuid():N}".Length)];

        var clientOptions = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(_options.Host, _options.Port)
            .WithCredentials(_options.Username, _options.Password)
            .WithTlsOptions(o => o.UseTls())
            .Build();

        var managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(clientOptions)
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
            .Build();

        var factory = new MqttClientFactory();
        _client = factory.CreateManagedMqttClient();

        _client.ConnectedAsync += args =>
        {
            logger.LogInformation("Connected to HiveMQ broker at {Host}:{Port}", _options.Host, _options.Port);
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += args =>
        {
            logger.LogWarning("Disconnected from HiveMQ broker: {Reason}", args.Reason);
            return Task.CompletedTask;
        };
        _client.ConnectingFailedAsync += args =>
        {
            logger.LogWarning(args.Exception, "Failed to connect to HiveMQ broker");
            return Task.CompletedTask;
        };

        await _client.StartAsync(managedOptions);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            await _client.StopAsync();
        }
    }
}
```

> Double-check the exact MQTTnet v5 API names used here (`MqttClientFactory`, `CreateManagedMqttClient`, `WithTlsOptions(o => o.UseTls())`, `ConnectedAsync`/`DisconnectedAsync`/`ConnectingFailedAsync` event names, `IManagedMqttClient.EnqueueAsync`) against the installed package version's IntelliSense/compiler errors — MQTTnet has changed these names across major versions, and the plan author verified them against v5.x docs/samples at write time but the installed version should be the source of truth. Adjust spelling/signatures as needed; the behavior (TLS, credentials, auto-reconnect, retained publish) is what matters, not the exact method names.

- [ ] **Step 3: Register MqttOptions, MqttConnectionService, and IMqttPublisher in `Program.cs`**

Add near the other service registrations in `Program.cs`:

```csharp
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.AddSingleton<MqttConnectionService>();
builder.Services.AddSingleton<IManagedMqttClientAccessor>(sp => sp.GetRequiredService<MqttConnectionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttConnectionService>());
builder.Services.AddSingleton<IMqttPublisher, HiveMqPublisher>();
```

(Registering `MqttConnectionService` as both the singleton accessor and the hosted service, via the same instance, avoids running two separate instances of it.)

- [ ] **Step 4: Build to verify everything compiles**

```powershell
dotnet build WhatDo.sln
```

Expected: `Build succeeded.` with 0 errors. Fix any MQTTnet v5 API name mismatches found here (see note in Step 2).

- [ ] **Step 5: Commit (Tasks 3 and 4 together)**

```powershell
git add -A
git commit -m "Add MQTTnet-based HiveMQ publisher and managed connection service"
```

---

### Task 5: Wire IMqttPublisher into the status-event endpoint

**Files:**
- Modify: `src/WhatDo.StatusService/Endpoints/StatusEventEndpoints.cs`
- Test: `src/WhatDo.StatusService.Tests/StatusEventEndpointTests.cs` (add cases)
- Create: `src/WhatDo.StatusService.Tests/FakeMqttPublisher.cs`

- [ ] **Step 1: Create a test double**

Create `src/WhatDo.StatusService.Tests/FakeMqttPublisher.cs`:

```csharp
using System.Collections.Concurrent;
using WhatDo.StatusService.Mqtt;

namespace WhatDo.StatusService.Tests;

public sealed class FakeMqttPublisher : IMqttPublisher
{
    public ConcurrentDictionary<string, InstanceStatusMessage> PublishedInstances { get; } = new();
    public ConcurrentBag<string> ClearedInstanceIds { get; } = new();
    public ConcurrentQueue<AggregateStatusMessage> PublishedAggregates { get; } = new();

    public Task PublishInstanceStatusAsync(string instanceId, InstanceStatusMessage message, CancellationToken cancellationToken = default)
    {
        PublishedInstances[instanceId] = message;
        return Task.CompletedTask;
    }

    public Task ClearInstanceStatusAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        PublishedInstances.TryRemove(instanceId, out _);
        ClearedInstanceIds.Add(instanceId);
        return Task.CompletedTask;
    }

    public Task PublishAggregateAsync(AggregateStatusMessage message, CancellationToken cancellationToken = default)
    {
        PublishedAggregates.Enqueue(message);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Register the fake in the test `WebApplicationFactory` so tests can inspect it**

In `StatusEventEndpointTests.cs`, add a constructor-level customization of the factory (replace `IMqttPublisher` registration with the fake), e.g.:

```csharp
private readonly WebApplicationFactory<Program> _factory;
private readonly FakeMqttPublisher _mqttPublisher = new();

public StatusEventEndpointTests(WebApplicationFactory<Program> factory)
{
    _factory = factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMqttPublisher>();
            services.AddSingleton<IMqttPublisher>(_mqttPublisher);
        });
    });
}
```

(Requires `using Microsoft.Extensions.DependencyInjection.Extensions;` for `RemoveAll`.)

- [ ] **Step 3: Write the failing tests for publish-on-upsert and clear-on-remove**

Add to `StatusEventEndpointTests.cs`:

```csharp
[Fact]
public async Task PostStatusEvent_WithCorrectSecret_PublishesToMqtt()
{
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

    await client.PostAsJsonAsync("/api/status-event", new
    {
        hook_event_name = "UserPromptSubmit",
        session_id = "a1b2c3d4e5f6",
        cwd = @"C:\repos\whatdo"
    });

    Assert.True(_mqttPublisher.PublishedInstances.ContainsKey("whatdo-a1b2c3d4"));
    Assert.Equal("working", _mqttPublisher.PublishedInstances["whatdo-a1b2c3d4"].Status);
    Assert.NotEmpty(_mqttPublisher.PublishedAggregates);
}

[Fact]
public async Task PostStatusEvent_SessionEnd_ClearsMqttTopic()
{
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

    await client.PostAsJsonAsync("/api/status-event", new
    {
        hook_event_name = "SessionStart",
        session_id = "deadbeef9999",
        cwd = @"C:\repos\whatdo"
    });

    await client.PostAsJsonAsync("/api/status-event", new
    {
        hook_event_name = "SessionEnd",
        session_id = "deadbeef9999",
        cwd = @"C:\repos\whatdo"
    });

    Assert.Contains("whatdo-deadbeef", _mqttPublisher.ClearedInstanceIds);
}
```

- [ ] **Step 4: Run tests to verify they fail**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: the two new tests fail (no MQTT publish happens yet since the endpoint doesn't call `IMqttPublisher`).

- [ ] **Step 5: Update `StatusEventEndpoints.cs` to publish after every store mutation**

Modify `MapStatusEventEndpoints` so the lambda takes an additional `IMqttPublisher mqttPublisher` parameter (minimal APIs resolve it from DI automatically), and after the existing `switch (result.Action)` block runs, publish accordingly:

```csharp
app.MapPost("/api/status-event", async (
    HttpRequest request,
    IInstanceStore store,
    IMqttPublisher mqttPublisher,
    IConfiguration config,
    ILoggerFactory loggerFactory) =>
{
    // ...existing secret validation and payload parsing unchanged...

    switch (result.Action)
    {
        case StatusAction.UpdateStatus:
            store.Upsert(instanceId, name, payload.Cwd ?? "", result.Status!.Value, payload.HookEventName ?? "");
            var updated = store.GetAll().First(i => i.InstanceId == instanceId);
            await mqttPublisher.PublishInstanceStatusAsync(instanceId, new InstanceStatusMessage(
                updated.Status.ToString().ToLowerInvariant() switch
                {
                    "pendinginput" => "pending_input",
                    var s => s
                },
                updated.Name, updated.Cwd, updated.LastEvent, updated.LastSeenUtc));
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
            aggregate.Status.ToString().ToLowerInvariant() switch { "pendinginput" => "pending_input", var s => s },
            aggregate.WorkingCount, aggregate.IdleCount, aggregate.PendingCount, aggregate.Total));
    }

    return Results.Ok();
});
```

Add `using WhatDo.StatusService.Mqtt;` to the top of the file. Consider extracting the `HookEventStatus` → wire-format string mapping (`pending_input` snake_case) into a small shared helper (e.g. a `ToWireString()` extension method on `HookEventStatus`) if it's needed in more than one place (it is: instance message + aggregate message) — add `src/WhatDo.StatusService/Status/HookEventStatusExtensions.cs` for this rather than repeating the inline switch twice.

- [ ] **Step 6: Run tests to verify they pass**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: all tests pass, including all pre-existing `StatusEventEndpointTests` (the fake publisher is a safe no-op for assertions it doesn't care about) and the two new MQTT tests.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Publish instance and aggregate status to MQTT on every status-event"
```

---

### Task 6: StaleInstanceSweepService (60s sweep, 3-minute staleness threshold)

**Files:**
- Modify: `src/WhatDo.StatusService/Status/IInstanceStore.cs` (if needed, to expose last-seen for sweep — already exposed via `InstanceStatusInfo.LastSeenUtc`, so likely no interface change needed)
- Create: `src/WhatDo.StatusService/Mqtt/StaleInstanceSweepService.cs`
- Test: `src/WhatDo.StatusService.Tests/StaleInstanceSweepServiceTests.cs`
- Modify: `src/WhatDo.StatusService/Program.cs`

- [ ] **Step 1: Write the failing unit test for the sweep's pure decision logic**

Rather than testing the `BackgroundService` timer loop directly (slow/flaky), extract the "which instances are stale" decision into a small pure function first so it's directly testable.

Create `src/WhatDo.StatusService.Tests/StaleInstanceSweepServiceTests.cs`:

```csharp
using WhatDo.StatusService.Mqtt;
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class StaleInstanceSweepServiceTests
{
    [Fact]
    public void FindStaleInstanceIds_InstanceOlderThanThreshold_IsReturned()
    {
        var now = DateTimeOffset.UtcNow;
        var instances = new[]
        {
            new InstanceStatusInfo("fresh", "fresh", "/a", HookEventStatus.Idle, "Stop", now.AddSeconds(-30)),
            new InstanceStatusInfo("stale", "stale", "/b", HookEventStatus.Working, "PostToolUse", now.AddMinutes(-5)),
        };

        var staleIds = StaleInstanceSweepService.FindStaleInstanceIds(instances, now, TimeSpan.FromMinutes(3));

        Assert.Equal(new[] { "stale" }, staleIds);
    }

    [Fact]
    public void FindStaleInstanceIds_NoneStale_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var instances = new[]
        {
            new InstanceStatusInfo("fresh", "fresh", "/a", HookEventStatus.Idle, "Stop", now.AddSeconds(-10)),
        };

        var staleIds = StaleInstanceSweepService.FindStaleInstanceIds(instances, now, TimeSpan.FromMinutes(3));

        Assert.Empty(staleIds);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: build FAILS — `StaleInstanceSweepService` does not exist.

- [ ] **Step 3: Create StaleInstanceSweepService with a public static pure method plus the BackgroundService loop**

Create `src/WhatDo.StatusService/Mqtt/StaleInstanceSweepService.cs`:

```csharp
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
        IReadOnlyCollection<InstanceStatusInfo> instances, DateTimeOffset now, TimeSpan staleThreshold)
        => instances
            .Where(i => now - i.LastSeenUtc > staleThreshold)
            .Select(i => i.InstanceId)
            .ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var staleIds = FindStaleInstanceIds(store.GetAll(), DateTimeOffset.UtcNow, StaleThreshold);

            foreach (var instanceId in staleIds)
            {
                logger.LogInformation("Removing stale instance {InstanceId} (no event for {Threshold})", instanceId, StaleThreshold);
                store.Remove(instanceId);
                await mqttPublisher.ClearInstanceStatusAsync(instanceId, stoppingToken);
            }

            if (staleIds.Count > 0)
            {
                var aggregate = AggregateCalculator.Calculate(store.GetAll());
                await mqttPublisher.PublishAggregateAsync(new AggregateStatusMessage(
                    aggregate.Status.ToWireString(),
                    aggregate.WorkingCount, aggregate.IdleCount, aggregate.PendingCount, aggregate.Total),
                    stoppingToken);
            }
        }
    }
}
```

(This assumes the `HookEventStatus.ToWireString()` extension from Task 5's Step 5 note was created — use it here too instead of repeating the inline switch a third time.)

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: both new tests pass.

- [ ] **Step 5: Register the hosted service in `Program.cs`**

```csharp
builder.Services.AddHostedService<StaleInstanceSweepService>();
```

- [ ] **Step 6: Build to verify everything compiles**

```powershell
dotnet build WhatDo.sln
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Add 60s staleness sweep removing instances idle for 3+ minutes"
```

---

### Task 7: HiveMQ Cloud Free-tier cluster provisioning (manual, one-time)

**Files:** none (manual Azure/HiveMQ portal steps + updating Azure app settings)

- [ ] **Step 1: Create a HiveMQ Cloud account and Free-tier cluster**

Go to <https://console.hivemq.cloud/>, sign up (or log in), create a new **Free** cluster (100 connections / 10GB/month). Note the cluster's TLS hostname (e.g. `xxxxxxxx.s2.eu.hivemq.cloud`) shown on the cluster's overview page.

- [ ] **Step 2: Create broker credentials**

In the cluster's **Access Management** tab, create a new credential set (username + password) scoped to this app (e.g. username `whatdo-status-service`). Save the password somewhere safe — HiveMQ only shows it once.

- [ ] **Step 3: Set the Azure Web App settings**

```powershell
az webapp config appsettings set `
    --name <your-app-name> `
    --resource-group whatdo-rg `
    --settings "Mqtt__Host=<your-cluster>.s2.eu.hivemq.cloud" "Mqtt__Port=8883" "Mqtt__Username=<username>" "Mqtt__Password=<password>"
```

(Or the Az PowerShell equivalent used earlier in this project: `Set-AzWebApp`/`Set-AzResource` against the app's `appsettings` — see `deploy/provision-azure-resources.ps1` for the established pattern of setting `StatusIngest__Secret` this way, and mirror it for these four new settings.)

- [ ] **Step 4: Restart the Web App so it picks up the new settings**

```powershell
az webapp restart --name <your-app-name> --resource-group whatdo-rg
```

- [ ] **Step 5: Verify in Log stream**

Portal → App Service → Monitoring → Log stream. Confirm the log line `Connected to HiveMQ broker at <host>:8883` appears (from `MqttConnectionService`), not `Failed to connect to HiveMQ broker` or `MQTT not configured`.

---

### Task 8: Manual end-to-end verification with MQTT Explorer

**Files:** none (manual verification only)

- [ ] **Step 1: Install MQTT Explorer** (<https://mqtt-explorer.com/>) and connect it to the same HiveMQ cluster (host/port/username/password from Task 7), with TLS enabled.

- [ ] **Step 2: Subscribe to `whatdo/status/#`**

- [ ] **Step 3: Send a manual test event to the deployed app** (same pattern as `hooks/README.md`'s manual verification section):

```powershell
Invoke-RestMethod -Method Post -Uri "https://<your-app>.azurewebsites.net/api/status-event" `
  -Headers @{ "X-Status-Secret" = "<your StatusIngest secret>" } `
  -ContentType "application/json" `
  -Body '{"hook_event_name":"UserPromptSubmit","session_id":"a1b2c3d4e5f6","cwd":"C:\\repos\\whatdo"}'
```

- [ ] **Step 4: Confirm in MQTT Explorer:**
  - A retained message appears at `whatdo/status/whatdo/a1b2c3d4` with `{"status":"working", ...}`.
  - `whatdo/status/_aggregate` updates to `{"status":"working","workingCount":1,...}`.

- [ ] **Step 5: Send a `SessionEnd` event for the same session, confirm:**
  - The per-instance topic's retained message is cleared (MQTT Explorer shows the topic disappear once empty-retained is processed).
  - `_aggregate` returns to `{"status":"idle","workingCount":0,...,"total":0}`.

- [ ] **Step 6: Disconnect and reconnect MQTT Explorer (simulating an ESP32 reboot) while an instance is mid-"working"; confirm the retained message is delivered immediately on resubscribe** — this is the core "poll-free, instant-on-reconnect" behavior the whole MQTT stage exists for.

- [ ] **Step 7: Wait 3+ minutes without sending any further events for a test instance (or temporarily lower `StaleThreshold` locally to something like 10 seconds for faster manual testing), and confirm the sweep removes it and clears its MQTT topic + updates the aggregate, matching Task 6's test expectations end-to-end.**

---

## Out of Scope (deferred to later stages)

- Azure Table Storage persistence (explicitly skipped per user request for this stage — Stage 1's in-memory store remains the source of truth; MQTT is purely a republish layer on top of it, so an app restart still clears all instance state today, MQTT or not).
- ESP32 firmware consuming `whatdo/status/_aggregate` directly from HiveMQ.
- Android app.
- Updating the dashboard page itself to subscribe to MQTT directly (the design spec's original intent was a Blazor Server page with a live SignalR-pushed MQTT subscription; the dashboard was since simplified to a plain HTTP-polling page per commit `25050a5`, which already works via `/api/status` regardless of this stage — re-wiring the dashboard onto MQTT, if still desired, should be a separate small follow-up plan rather than bundled here).
