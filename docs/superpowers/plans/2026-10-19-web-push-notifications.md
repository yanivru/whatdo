# Web Push Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Send an Android-capable Web Push notification when a Copilot CLI instance transitions into `PendingInput`.

**Architecture:** A `PushNotifier` (behind `IPushNotifier`) sends to all stored browser subscriptions through an `IPushSender` wrapping the `WebPush` NuGet client. `StatusEventEndpoints` detects the transition into `PendingInput` using a new `IInstanceStore.TryGet` and calls the notifier. Client-side, a service worker plus `push.js` handle subscribe/permission and display.

**Tech Stack:** .NET 8 minimal APIs, Blazor (static SSR pages), `WebPush` NuGet package (VAPID), xUnit, `WebApplicationFactory`.

Spec: `docs/superpowers/specs/2026-10-19-web-push-notifications-design.md`

All commands run from the repo root. Test command: `dotnet test src\WhatDo.StatusService.Tests`.

## File Structure

- Modify `src/WhatDo.StatusService/Status/IInstanceStore.cs`, `InMemoryInstanceStore.cs` — add `TryGet`.
- Create `src/WhatDo.StatusService/Push/PushOptions.cs` — config (`Push` section).
- Create `src/WhatDo.StatusService/Push/PushSubscriptionInfo.cs` — subscription record.
- Create `src/WhatDo.StatusService/Push/IPushSubscriptionStore.cs`, `InMemoryPushSubscriptionStore.cs` — storage with cap.
- Create `src/WhatDo.StatusService/Push/IPushSender.cs`, `WebPushSender.cs` — send wrapper + result enum.
- Create `src/WhatDo.StatusService/Push/IPushNotifier.cs`, `PushNotifier.cs` — fan-out + expiry cleanup.
- Create `src/WhatDo.StatusService/Endpoints/PushEndpoints.cs` — public-key/subscribe/unsubscribe.
- Modify `src/WhatDo.StatusService/Endpoints/StatusEventEndpoints.cs`, `Program.cs` — wiring.
- Create `src/WhatDo.StatusService/wwwroot/sw.js`, `push.js`, `manifest.webmanifest`; modify `Components/App.razor`, `Components/Pages/Home.razor`.
- Tests in `src/WhatDo.StatusService.Tests/`: `InMemoryInstanceStoreTests.cs` (modify), `InMemoryPushSubscriptionStoreTests.cs`, `PushNotifierTests.cs`, `PushEndpointsTests.cs`, `FakePushSender.cs`, `FakePushNotifier.cs`, `StatusEventEndpointTests.cs` (modify).
- Modify `deploy/README.md` — VAPID key setup.

---

### Task 1: `IInstanceStore.TryGet`

**Files:**
- Modify: `src/WhatDo.StatusService/Status/IInstanceStore.cs`
- Modify: `src/WhatDo.StatusService/Status/InMemoryInstanceStore.cs`
- Test: `src/WhatDo.StatusService.Tests/InMemoryInstanceStoreTests.cs`

- [ ] **Step 1: Write the failing tests** — append inside the `InMemoryInstanceStoreTests` class:

```csharp
    [Fact]
    public void TryGet_ExistingInstance_ReturnsIt()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        var found = store.TryGet("whatdo-a1b2c3d4", out var info);

        Assert.True(found);
        Assert.Equal(HookEventStatus.Working, info!.Status);
    }

    [Fact]
    public void TryGet_UnknownInstance_ReturnsFalse()
    {
        var store = new InMemoryInstanceStore();

        var found = store.TryGet("nope", out var info);

        Assert.False(found);
        Assert.Null(info);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter InMemoryInstanceStoreTests`
Expected: build FAIL — `TryGet` not defined.

- [ ] **Step 3: Implement.** In `IInstanceStore.cs` add (add `using System.Diagnostics.CodeAnalysis;` at the top):

```csharp
    bool TryGet(string instanceId, [NotNullWhen(true)] out InstanceStatusInfo? info);
```

In `InMemoryInstanceStore.cs` add (same using at the top):

```csharp
    public bool TryGet(string instanceId, [NotNullWhen(true)] out InstanceStatusInfo? info)
        => _instances.TryGetValue(instanceId, out info);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter InMemoryInstanceStoreTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/WhatDo.StatusService src/WhatDo.StatusService.Tests
git commit -m "feat: add IInstanceStore.TryGet"
```

---

### Task 2: Options, subscription model, subscription store

**Files:**
- Modify: `src/WhatDo.StatusService/WhatDo.StatusService.csproj`
- Create: `src/WhatDo.StatusService/Push/PushOptions.cs`, `PushSubscriptionInfo.cs`, `IPushSubscriptionStore.cs`, `InMemoryPushSubscriptionStore.cs`
- Test: `src/WhatDo.StatusService.Tests/InMemoryPushSubscriptionStoreTests.cs`

- [ ] **Step 1: Add the package**

Run: `dotnet add src\WhatDo.StatusService package WebPush`
Expected: package added to the csproj.

- [ ] **Step 2: Create the model and options**

`Push/PushSubscriptionInfo.cs`:

```csharp
namespace WhatDo.StatusService.Push;

public sealed record PushSubscriptionInfo(string Endpoint, string P256dh, string Auth);
```

`Push/PushOptions.cs`:

```csharp
namespace WhatDo.StatusService.Push;

public sealed class PushOptions
{
    public const string SectionName = "Push";

    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string Subject { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey) &&
        !string.IsNullOrWhiteSpace(PrivateKey) &&
        !string.IsNullOrWhiteSpace(Subject);
}
```

`Push/IPushSubscriptionStore.cs`:

```csharp
namespace WhatDo.StatusService.Push;

public interface IPushSubscriptionStore
{
    /// <summary>Adds or refreshes a subscription. Returns false when the cap is reached for a new endpoint.</summary>
    bool TryAdd(PushSubscriptionInfo subscription);

    void Remove(string endpoint);

    IReadOnlyList<PushSubscriptionInfo> GetAll();
}
```

- [ ] **Step 3: Write the failing tests** — `InMemoryPushSubscriptionStoreTests.cs`:

```csharp
using WhatDo.StatusService.Push;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class InMemoryPushSubscriptionStoreTests
{
    private static PushSubscriptionInfo Sub(string endpoint, string auth = "auth")
        => new(endpoint, "p256dh", auth);

    [Fact]
    public void TryAdd_NewSubscription_AppearsInGetAll()
    {
        var store = new InMemoryPushSubscriptionStore();

        Assert.True(store.TryAdd(Sub("https://push.example/1")));

        Assert.Single(store.GetAll());
    }

    [Fact]
    public void TryAdd_SameEndpoint_ReplacesInsteadOfDuplicating()
    {
        var store = new InMemoryPushSubscriptionStore();
        store.TryAdd(Sub("https://push.example/1", "old"));

        store.TryAdd(Sub("https://push.example/1", "new"));

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("new", all[0].Auth);
    }

    [Fact]
    public void TryAdd_AtCap_RejectsNewButAllowsRefreshOfExisting()
    {
        var store = new InMemoryPushSubscriptionStore(maxSubscriptions: 2);
        store.TryAdd(Sub("https://push.example/1"));
        store.TryAdd(Sub("https://push.example/2"));

        Assert.False(store.TryAdd(Sub("https://push.example/3")));
        Assert.True(store.TryAdd(Sub("https://push.example/1", "refreshed")));
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void Remove_UnknownEndpoint_DoesNotThrow()
    {
        var store = new InMemoryPushSubscriptionStore();

        Assert.Null(Record.Exception(() => store.Remove("https://push.example/none")));
    }

    [Fact]
    public void Remove_ExistingEndpoint_RemovesIt()
    {
        var store = new InMemoryPushSubscriptionStore();
        store.TryAdd(Sub("https://push.example/1"));

        store.Remove("https://push.example/1");

        Assert.Empty(store.GetAll());
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter InMemoryPushSubscriptionStoreTests`
Expected: build FAIL — `InMemoryPushSubscriptionStore` not defined.

- [ ] **Step 5: Implement** — `Push/InMemoryPushSubscriptionStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace WhatDo.StatusService.Push;

public sealed class InMemoryPushSubscriptionStore : IPushSubscriptionStore
{
    private readonly ConcurrentDictionary<string, PushSubscriptionInfo> _subscriptions = new();
    private readonly object _addLock = new();
    private readonly int _maxSubscriptions;

    public InMemoryPushSubscriptionStore(int maxSubscriptions = 50)
    {
        _maxSubscriptions = maxSubscriptions;
    }

    public bool TryAdd(PushSubscriptionInfo subscription)
    {
        lock (_addLock)
        {
            if (!_subscriptions.ContainsKey(subscription.Endpoint) && _subscriptions.Count >= _maxSubscriptions)
            {
                return false;
            }

            _subscriptions[subscription.Endpoint] = subscription;
            return true;
        }
    }

    public void Remove(string endpoint)
    {
        _subscriptions.TryRemove(endpoint, out _);
    }

    public IReadOnlyList<PushSubscriptionInfo> GetAll()
        => _subscriptions.Values.ToList();
}
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter InMemoryPushSubscriptionStoreTests`
Expected: PASS (5 tests).

- [ ] **Step 7: Commit**

```bash
git add src/WhatDo.StatusService src/WhatDo.StatusService.Tests
git commit -m "feat: add push options, subscription model and in-memory store"
```

---

### Task 3: Sender abstraction, `WebPushSender`, `PushNotifier`

**Files:**
- Create: `src/WhatDo.StatusService/Push/IPushSender.cs`, `WebPushSender.cs`, `IPushNotifier.cs`, `PushNotifier.cs`
- Create: `src/WhatDo.StatusService.Tests/FakePushSender.cs`
- Test: `src/WhatDo.StatusService.Tests/PushNotifierTests.cs`

- [ ] **Step 1: Create the sender abstraction** — `Push/IPushSender.cs`:

```csharp
namespace WhatDo.StatusService.Push;

public enum PushSendResult
{
    Sent,
    Expired,
    Failed
}

public interface IPushSender
{
    Task<PushSendResult> SendAsync(PushSubscriptionInfo subscription, string payload, CancellationToken cancellationToken = default);
}
```

`Push/IPushNotifier.cs`:

```csharp
namespace WhatDo.StatusService.Push;

public interface IPushNotifier
{
    Task NotifyPendingInputAsync(string instanceId, string name);
}
```

- [ ] **Step 2: Create the test fake** — `FakePushSender.cs`:

```csharp
using WhatDo.StatusService.Push;

namespace WhatDo.StatusService.Tests;

public sealed class FakePushSender : IPushSender
{
    public List<(PushSubscriptionInfo Subscription, string Payload)> Sent { get; } = new();
    public Func<PushSubscriptionInfo, PushSendResult> ResultFor { get; set; } = _ => PushSendResult.Sent;
    public bool Throw { get; set; }

    public Task<PushSendResult> SendAsync(PushSubscriptionInfo subscription, string payload, CancellationToken cancellationToken = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("boom");
        }

        Sent.Add((subscription, payload));
        return Task.FromResult(ResultFor(subscription));
    }
}
```

- [ ] **Step 3: Write the failing tests** — `PushNotifierTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WhatDo.StatusService.Push;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class PushNotifierTests
{
    private readonly InMemoryPushSubscriptionStore _store = new();
    private readonly FakePushSender _sender = new();
    private readonly PushNotifier _notifier;

    public PushNotifierTests()
    {
        _notifier = new PushNotifier(_store, _sender, NullLogger<PushNotifier>.Instance);
    }

    [Fact]
    public async Task Notify_SendsPayloadToEverySubscription()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/1", "k", "a"));
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/2", "k", "a"));

        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        Assert.Equal(2, _sender.Sent.Count);
        using var doc = JsonDocument.Parse(_sender.Sent[0].Payload);
        Assert.Equal("Copilot needs input", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("whatdo is waiting for you", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal("whatdo-a1b2c3d4", doc.RootElement.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task Notify_NoSubscriptions_DoesNothing()
    {
        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Notify_ExpiredSubscription_IsRemoved()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/gone", "k", "a"));
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/ok", "k", "a"));
        _sender.ResultFor = s => s.Endpoint.EndsWith("gone") ? PushSendResult.Expired : PushSendResult.Sent;

        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        var remaining = Assert.Single(_store.GetAll());
        Assert.Equal("https://push.example/ok", remaining.Endpoint);
    }

    [Fact]
    public async Task Notify_FailedSend_KeepsSubscription()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/1", "k", "a"));
        _sender.ResultFor = _ => PushSendResult.Failed;

        await _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo");

        Assert.Single(_store.GetAll());
    }

    [Fact]
    public async Task Notify_SenderThrows_DoesNotPropagate()
    {
        _store.TryAdd(new PushSubscriptionInfo("https://push.example/1", "k", "a"));
        _sender.Throw = true;

        var exception = await Record.ExceptionAsync(() => _notifier.NotifyPendingInputAsync("whatdo-a1b2c3d4", "whatdo"));

        Assert.Null(exception);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter PushNotifierTests`
Expected: build FAIL — `PushNotifier` not defined.

- [ ] **Step 5: Implement `PushNotifier`** — `Push/PushNotifier.cs`:

```csharp
using System.Text.Json;

namespace WhatDo.StatusService.Push;

public sealed class PushNotifier : IPushNotifier
{
    private readonly IPushSubscriptionStore _store;
    private readonly IPushSender _sender;
    private readonly ILogger<PushNotifier> _logger;

    public PushNotifier(IPushSubscriptionStore store, IPushSender sender, ILogger<PushNotifier> logger)
    {
        _store = store;
        _sender = sender;
        _logger = logger;
    }

    public async Task NotifyPendingInputAsync(string instanceId, string name)
    {
        var subscriptions = _store.GetAll();
        if (subscriptions.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = "Copilot needs input",
            body = $"{name} is waiting for you",
            tag = instanceId
        });

        foreach (var subscription in subscriptions)
        {
            try
            {
                var result = await _sender.SendAsync(subscription, payload);
                if (result == PushSendResult.Expired)
                {
                    _store.Remove(subscription.Endpoint);
                }
                else if (result == PushSendResult.Failed)
                {
                    _logger.LogWarning("Push send failed for subscription {Endpoint}", subscription.Endpoint);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Push send threw for subscription {Endpoint}", subscription.Endpoint);
            }
        }
    }
}
```

- [ ] **Step 6: Implement `WebPushSender`** — `Push/WebPushSender.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using WebPush;

namespace WhatDo.StatusService.Push;

public sealed class WebPushSender : IPushSender
{
    private readonly WebPushClient _client = new();
    private readonly VapidDetails _vapid;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(IOptions<PushOptions> options, ILogger<WebPushSender> logger)
    {
        var o = options.Value;
        _vapid = new VapidDetails(o.Subject, o.PublicKey, o.PrivateKey);
        _logger = logger;
    }

    public async Task<PushSendResult> SendAsync(PushSubscriptionInfo subscription, string payload, CancellationToken cancellationToken = default)
    {
        try
        {
            var target = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
            await _client.SendNotificationAsync(target, payload, _vapid, cancellationToken);
            return PushSendResult.Sent;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushSendResult.Expired;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web Push delivery failed");
            return PushSendResult.Failed;
        }
    }
}
```

If the `SendNotificationAsync` overload signature differs in the installed `WebPush` version, adjust the call (it must pass subscription, payload, `VapidDetails`, and a token) — build will tell you.

- [ ] **Step 7: Run to verify pass**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter PushNotifierTests`
Expected: PASS (5 tests).

- [ ] **Step 8: Commit**

```bash
git add src/WhatDo.StatusService src/WhatDo.StatusService.Tests
git commit -m "feat: add PushNotifier and WebPushSender"
```

---

### Task 4: Push endpoints and DI wiring

**Files:**
- Create: `src/WhatDo.StatusService/Endpoints/PushEndpoints.cs`
- Modify: `src/WhatDo.StatusService/Program.cs`
- Test: `src/WhatDo.StatusService.Tests/PushEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests** — `PushEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WhatDo.StatusService.Push;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class PushEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _baseFactory;

    public PushEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
    }

    private WebApplicationFactory<Program> ConfiguredFactory() =>
        _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Push:PublicKey", "test-public-key");
            builder.UseSetting("Push:PrivateKey", "test-private-key");
            builder.UseSetting("Push:Subject", "mailto:test@example.com");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(new FakePushSender());
            });
        });

    private static object ValidBody(string endpoint = "https://push.example/abc") =>
        new { endpoint, keys = new { p256dh = "p", auth = "a" } };

    [Fact]
    public async Task PublicKey_NotConfigured_Returns404()
    {
        var client = _baseFactory.CreateClient();

        var response = await client.GetAsync("/api/push/public-key");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicKey_Configured_ReturnsKey()
    {
        var client = ConfiguredFactory().CreateClient();

        var response = await client.GetAsync("/api/push/public-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("test-public-key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Subscribe_Valid_StoresSubscription()
    {
        var factory = ConfiguredFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/push/subscribe", ValidBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = factory.Services.GetRequiredService<IPushSubscriptionStore>();
        Assert.Single(store.GetAll());
    }

    [Theory]
    [InlineData("http://push.example/abc")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Subscribe_InvalidEndpoint_Returns400(string endpoint)
    {
        var client = ConfiguredFactory().CreateClient();

        var response = await client.PostAsJsonAsync("/api/push/subscribe", ValidBody(endpoint));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_MissingKeys_Returns400()
    {
        var client = ConfiguredFactory().CreateClient();

        var response = await client.PostAsJsonAsync("/api/push/subscribe", new { endpoint = "https://push.example/abc" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_NotConfigured_Returns404()
    {
        var client = _baseFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/push/subscribe", ValidBody());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unsubscribe_RemovesSubscription()
    {
        var factory = ConfiguredFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/push/subscribe", ValidBody());

        var response = await client.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint = "https://push.example/abc" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = factory.Services.GetRequiredService<IPushSubscriptionStore>();
        Assert.Empty(store.GetAll());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter PushEndpointsTests`
Expected: FAIL (endpoints return 404 / services missing).

- [ ] **Step 3: Implement endpoints** — `Endpoints/PushEndpoints.cs`:

```csharp
using Microsoft.Extensions.Options;
using WhatDo.StatusService.Push;

namespace WhatDo.StatusService.Endpoints;

public static class PushEndpoints
{
    public sealed record SubscribeKeys(string? P256dh, string? Auth);
    public sealed record SubscribeRequest(string? Endpoint, SubscribeKeys? Keys);
    public sealed record UnsubscribeRequest(string? Endpoint);

    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/push/public-key", (IOptions<PushOptions> options) =>
            options.Value.IsConfigured
                ? Results.Text(options.Value.PublicKey)
                : Results.NotFound());

        app.MapPost("/api/push/subscribe", (SubscribeRequest request, IOptions<PushOptions> options, IPushSubscriptionStore store) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.NotFound();
            }

            // https-only: the server will POST to this URL, so don't accept arbitrary schemes/hosts like http or file.
            if (!IsHttpsUrl(request.Endpoint) ||
                string.IsNullOrWhiteSpace(request.Keys?.P256dh) ||
                string.IsNullOrWhiteSpace(request.Keys?.Auth))
            {
                return Results.BadRequest();
            }

            return store.TryAdd(new PushSubscriptionInfo(request.Endpoint!, request.Keys!.P256dh!, request.Keys.Auth!))
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status429TooManyRequests);
        });

        app.MapPost("/api/push/unsubscribe", (UnsubscribeRequest request, IOptions<PushOptions> options, IPushSubscriptionStore store) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return Results.BadRequest();
            }

            store.Remove(request.Endpoint);
            return Results.Ok();
        });
    }

    private static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
```

- [ ] **Step 4: Wire DI** — in `Program.cs`, add `using WhatDo.StatusService.Push;` and after `AddHostedService<StaleInstanceSweepService>();`:

```csharp
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.SectionName));
builder.Services.AddSingleton<IPushSubscriptionStore, InMemoryPushSubscriptionStore>();
builder.Services.AddSingleton<IPushSender, WebPushSender>();
builder.Services.AddSingleton<IPushNotifier, PushNotifier>();
```

and after `app.MapStatusEventEndpoints();`:

```csharp
app.MapPushEndpoints();
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter PushEndpointsTests`
Expected: PASS (9 tests). Note: with empty keys `WebPushSender` is still constructed lazily only when resolved; the NotConfigured tests never resolve it. If any test fails because `VapidDetails` throws on empty keys, make `WebPushSender` build `_vapid` lazily on first `SendAsync` (and only when `IsConfigured`).

- [ ] **Step 6: Commit**

```bash
git add src/WhatDo.StatusService src/WhatDo.StatusService.Tests
git commit -m "feat: add push subscribe/unsubscribe endpoints"
```

---

### Task 5: Trigger notification on transition to `PendingInput`

**Files:**
- Create: `src/WhatDo.StatusService.Tests/FakePushNotifier.cs`
- Modify: `src/WhatDo.StatusService/Endpoints/StatusEventEndpoints.cs`
- Test: `src/WhatDo.StatusService.Tests/StatusEventEndpointTests.cs`

- [ ] **Step 1: Create the fake** — `FakePushNotifier.cs`:

```csharp
using WhatDo.StatusService.Push;

namespace WhatDo.StatusService.Tests;

public sealed class FakePushNotifier : IPushNotifier
{
    public List<(string InstanceId, string Name)> Notified { get; } = new();

    public Task NotifyPendingInputAsync(string instanceId, string name)
    {
        Notified.Add((instanceId, name));
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Write the failing tests.** In `StatusEventEndpointTests.cs`: add `using WhatDo.StatusService.Push;`, a field `private readonly FakePushNotifier _pushNotifier = new();`, in the constructor's `ConfigureServices` add:

```csharp
                services.RemoveAll<IPushNotifier>();
                services.AddSingleton<IPushNotifier>(_pushNotifier);
```

and append these tests:

```csharp
    [Fact]
    public async Task PostStatusEvent_TransitionToPendingInput_NotifiesOnce()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "aaaa1111bbbb",
            cwd = @"C:\repos\whatdo"
        });
        Assert.Empty(_pushNotifier.Notified);

        // Two notification events in a row: only the transition should notify.
        for (var i = 0; i < 2; i++)
        {
            await client.PostAsJsonAsync("/api/status-event", new
            {
                hook_event_name = "Notification",
                sessionId = "aaaa1111bbbb",
                cwd = @"C:\repos\whatdo",
                notification_type = "permission_prompt"
            });
        }

        var notified = Assert.Single(_pushNotifier.Notified);
        Assert.Equal("whatdo-aaaa1111", notified.InstanceId);
        Assert.Equal("whatdo", notified.Name);
    }

    [Fact]
    public async Task PostStatusEvent_WorkingEvent_DoesNotNotify()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "cccc2222dddd",
            cwd = @"C:\repos\whatdo"
        });

        Assert.Empty(_pushNotifier.Notified);
    }

    [Fact]
    public async Task PostStatusEvent_PendingInputThenWorkingThenPendingInput_NotifiesTwice()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        object Notification() => new
        {
            hook_event_name = "Notification",
            sessionId = "eeee3333ffff",
            cwd = @"C:\repos\whatdo",
            notification_type = "permission_prompt"
        };

        await client.PostAsJsonAsync("/api/status-event", Notification());
        await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "eeee3333ffff",
            cwd = @"C:\repos\whatdo"
        });
        await client.PostAsJsonAsync("/api/status-event", Notification());

        Assert.Equal(2, _pushNotifier.Notified.Count);
    }
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test src\WhatDo.StatusService.Tests --filter StatusEventEndpointTests`
Expected: the three new tests FAIL (no notifications recorded); existing tests still pass.

- [ ] **Step 4: Implement.** In `StatusEventEndpoints.cs`:

Add `using WhatDo.StatusService.Push;`. Add `IPushNotifier pushNotifier` to the handler parameters (after `IMqttPublisher mqttPublisher`).

Replace the `store.Upsert(...)` line in the `UpdateStatus` case with:

```csharp
                    var wasPendingInput = store.TryGet(instanceId, out var previous) &&
                                          previous.Status == HookEventStatus.PendingInput;
                    store.Upsert(instanceId, name, cwd, result.Status!.Value, lastEvent);
```

(`cwd` and `lastEvent` declarations stay above it; remove the `!` duplicate use accordingly: the existing line used `result.Status!.Value`.)

After the `await mqttPublisher.PublishInstanceStatusAsync(...)` call, still inside the `UpdateStatus` case and before `break;`, add:

```csharp
                    if (result.Status.Value == HookEventStatus.PendingInput && !wasPendingInput)
                    {
                        await pushNotifier.NotifyPendingInputAsync(instanceId, name);
                    }
```

- [ ] **Step 5: Run the full test project**

Run: `dotnet test src\WhatDo.StatusService.Tests`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WhatDo.StatusService src/WhatDo.StatusService.Tests
git commit -m "feat: notify on transition into PendingInput"
```

---

### Task 6: Client — manifest, service worker, subscribe UI

**Files:**
- Create: `src/WhatDo.StatusService/wwwroot/manifest.webmanifest`, `sw.js`, `push.js`
- Modify: `src/WhatDo.StatusService/Components/App.razor`, `Components/Pages/Home.razor`

No automated tests (browser APIs); verify manually in Step 6.

- [ ] **Step 1: Create `wwwroot/manifest.webmanifest`**

```json
{
  "name": "WhatDo - Copilot CLI Status",
  "short_name": "WhatDo",
  "start_url": "/",
  "display": "standalone",
  "background_color": "#ffffff",
  "theme_color": "#0d6efd",
  "icons": [
    { "src": "favicon.png", "sizes": "any", "type": "image/png" }
  ]
}
```

- [ ] **Step 2: Create `wwwroot/sw.js`**

```javascript
self.addEventListener('push', event => {
    let data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch {
        data = {};
    }

    const title = data.title || 'Copilot needs input';
    event.waitUntil(self.registration.showNotification(title, {
        body: data.body || '',
        tag: data.tag,
        icon: '/favicon.png',
        renotify: true
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of windows) {
            if ('focus' in client) {
                return client.focus();
            }
        }
        return self.clients.openWindow('/');
    })());
});
```

- [ ] **Step 3: Create `wwwroot/push.js`**

```javascript
(function () {
    function urlBase64ToUint8Array(base64) {
        const padding = '='.repeat((4 - (base64.length % 4)) % 4);
        const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
        return Uint8Array.from(raw, c => c.charCodeAt(0));
    }

    async function post(url, body) {
        const response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        if (!response.ok) {
            throw new Error(url + ' returned ' + response.status);
        }
    }

    async function init() {
        const button = document.getElementById('push-toggle');
        const status = document.getElementById('push-status');
        if (!button || button.dataset.initialized) {
            return;
        }
        button.dataset.initialized = 'true';

        if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
            status.textContent = 'Notifications are not supported in this browser.';
            return;
        }

        const keyResponse = await fetch('/api/push/public-key');
        if (!keyResponse.ok) {
            status.textContent = 'Notifications are not configured on the server.';
            return;
        }
        const publicKey = await keyResponse.text();

        const registration = await navigator.serviceWorker.register('/sw.js');
        let subscription = await registration.pushManager.getSubscription();

        function render() {
            button.hidden = Notification.permission === 'denied';
            button.textContent = subscription ? 'Disable notifications' : 'Enable notifications';
            status.textContent = Notification.permission === 'denied'
                ? 'Notifications are blocked in browser settings.'
                : (subscription ? 'Notifications on for this device.' : '');
        }

        // The server store is in-memory; re-register the existing subscription after a restart.
        if (subscription) {
            await post('/api/push/subscribe', subscription.toJSON()).catch(() => { });
        }
        render();

        button.addEventListener('click', async () => {
            button.disabled = true;
            try {
                if (subscription) {
                    await post('/api/push/unsubscribe', { endpoint: subscription.endpoint });
                    await subscription.unsubscribe();
                    subscription = null;
                } else {
                    const permission = await Notification.requestPermission();
                    if (permission === 'granted') {
                        subscription = await registration.pushManager.subscribe({
                            userVisibleOnly: true,
                            applicationServerKey: urlBase64ToUint8Array(publicKey)
                        });
                        await post('/api/push/subscribe', subscription.toJSON());
                    }
                }
            } catch (e) {
                status.textContent = 'Failed: ' + e.message;
            } finally {
                button.disabled = false;
                render();
            }
        });
    }

    document.addEventListener('DOMContentLoaded', init);
    if (document.readyState !== 'loading') {
        init();
    }
    if (window.Blazor) {
        window.Blazor.addEventListener('enhancedload', init);
    }
})();
```

- [ ] **Step 4: Update `Components/App.razor`** — in `<head>` after the favicon link add:

```html
    <link rel="manifest" href="manifest.webmanifest" />
    <meta name="theme-color" content="#0d6efd" />
```

and after the `blazor.web.js` script add:

```html
    <script src="push.js"></script>
```

- [ ] **Step 5: Update `Components/Pages/Home.razor`** — directly under the `<h1>` add:

```html
<div class="mb-3">
    <button id="push-toggle" class="btn btn-outline-primary btn-sm" type="button" hidden>Enable notifications</button>
    <span id="push-status" class="ms-2 text-muted"></span>
</div>
```

(`push.js` unhides the button only after confirming support and server config. Because `hidden` is set in markup, `render()` in `push.js` must set `button.hidden = false` when not denied — it already does via `button.hidden = Notification.permission === 'denied'`.)

- [ ] **Step 6: Manual verification**

1. Generate keys: `npx web-push generate-vapid-keys`.
2. `dotnet user-secrets set "Push:PublicKey" "<public>" --project src\WhatDo.StatusService` (and `Push:PrivateKey`, `Push:Subject` = `mailto:you@example.com`).
3. `dotnet run --project src\WhatDo.StatusService`; open `http://localhost:<port>` in Chrome desktop; click **Enable notifications**, allow.
4. Trigger: `curl -X POST http://localhost:<port>/api/status-event -H "Content-Type: application/json" -H "X-Status-Secret: dev-secret-change-me" -d '{"hook_event_name":"Notification","sessionId":"abcdef123456","cwd":"C:\\repos\\demo","notification_type":"permission_prompt"}'`
5. Expected: desktop notification "Copilot needs input / demo is waiting for you". Repeat the same curl: no second notification. Tapping the notification focuses the site.
6. Android: open the deployed HTTPS site in Chrome, enable, lock the phone, send the curl against the deployed URL; expect a notification on the lock screen.

- [ ] **Step 7: Commit**

```bash
git add src/WhatDo.StatusService
git commit -m "feat: add service worker, manifest and notification toggle UI"
```

---

### Task 7: Deployment docs

**Files:**
- Modify: `deploy/README.md`

- [ ] **Step 1:** Read `deploy/README.md` and find how app settings are configured on the Azure App Service (for example the existing `StatusIngest__Secret` or `Mqtt__*` settings). Add a "Web Push notifications" section after it with:

```markdown
## Web Push notifications

Generate a VAPID key pair once: `npx web-push generate-vapid-keys`.

Set these app settings (App Service uses `__` for nesting); leave them unset to disable push:

- `Push__PublicKey`
- `Push__PrivateKey` (secret)
- `Push__Subject` (for example `mailto:you@example.com`)

Locally use `dotnet user-secrets set "Push:PublicKey" "..." --project src\WhatDo.StatusService`.

Subscriptions are held in memory, so they are lost on restart or redeploy; each browser re-registers when the page is next opened.
```

- [ ] **Step 2: Commit**

```bash
git add deploy/README.md
git commit -m "docs: document Web Push VAPID setup"
```

---

## Self-Review

- **Spec coverage:** manifest/sw/push.js/Home button (Task 6); `PushOptions` disabled-when-empty (Tasks 2, 4); store + cap (Task 2); sender with 404/410 expiry (Task 3); notifier fan-out and swallowed errors (Task 3); endpoints incl. 400 and cap (Task 4); transition-only trigger via `TryGet` (Tasks 1, 5); re-subscribe on load (Task 6 `push.js`); docs (Task 7). The spec's "protected like existing endpoints" is intentionally not done: the subscribe endpoints are public, as the spec's Security section states, mitigated by the cap and the https-only endpoint check (added here beyond the spec to prevent the server being pointed at arbitrary URLs).
- **Type consistency:** `PushSubscriptionInfo(Endpoint, P256dh, Auth)`, `IPushSender.SendAsync`, `PushSendResult`, `IPushNotifier.NotifyPendingInputAsync(instanceId, name)`, `IInstanceStore.TryGet` are used identically across tasks.
