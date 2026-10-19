# WhatDo Status Service — Stage 1: Hooks + Minimal Dashboard (No Persistence, No MQTT) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the smallest possible end-to-end slice: Copilot CLI hooks POST status events to a local ASP.NET Core app, which holds instance status in memory and shows it live on a Blazor Server page. No Azure Table Storage, no MQTT, no HiveMQ, no deployment — those are later stages, planned separately once this stage is manually verified.

**Architecture:** One ASP.NET Core project (`WhatDo.StatusService`, Blazor Web App template, Interactive Server render mode) exposes `POST /api/status-event` (the hook ingest endpoint) and a single dashboard page at `/`. An in-memory `ConcurrentDictionary`-backed store (`InMemoryInstanceStore`) is the only state; the dashboard polls it every 2 seconds via a `PeriodicTimer` and re-renders. A pure `StatusMapper` function maps each Copilot CLI hook event to a status (or "remove instance"), decoupled from the endpoint and store so it can be unit tested in isolation.

**Tech Stack:** .NET 8, ASP.NET Core Blazor Web App (Interactive Server), xUnit, `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory) for integration tests.

---

## Reference: Status Mapping (from design spec)

| `hook_event_name` | `notification_type` | Action |
|---|---|---|
| `SessionStart` | — | Update status → `Idle` |
| `UserPromptSubmit` | — | Update status → `Working` |
| `PreToolUse` | — | Update status → `Working` |
| `PostToolUse` | — | Update status → `Working` |
| `PostToolUseFailure` | — | Update status → `Working` |
| `Stop` | — | Update status → `Idle` |
| `Notification` | `permission_prompt`, `elicitation_dialog` | Update status → `PendingInput` |
| `Notification` | `agent_idle`, `shell_completed`, `agent_completed`, `shell_detached_completed` | Update status → `Idle` |
| `Notification` | anything else | Ignore |
| `SessionEnd` | — | Remove instance |
| anything else | — | Ignore |

Instance ID = `<sanitized-last-folder-of-cwd>-<first-8-chars-of-sessionId>` (e.g. `whatdo-a1b2c3d4`).

---

### Task 1: Scaffold solution and projects

**Files:**
- Create: `WhatDo.sln`
- Create: `src/WhatDo.StatusService/` (via `dotnet new blazor`)
- Create: `src/WhatDo.StatusService.Tests/` (via `dotnet new xunit`)

- [ ] **Step 1: Create the solution file**

Run from the repo root (`C:\Users\me\source\repos\whatdo`):

```powershell
dotnet new sln -n WhatDo
```

Expected: `The template "Solution File" was created successfully.` and a `WhatDo.sln` file appears in the repo root.

- [ ] **Step 2: Scaffold the web app project**

```powershell
dotnet new blazor -n WhatDo.StatusService -o src/WhatDo.StatusService --interactivity Server -f net8.0
```

Expected: `The template "Blazor Web App" was created successfully.` and `src/WhatDo.StatusService/` contains `Program.cs`, `Components/`, `WhatDo.StatusService.csproj`, etc.

- [ ] **Step 3: Scaffold the test project**

```powershell
dotnet new xunit -n WhatDo.StatusService.Tests -o src/WhatDo.StatusService.Tests -f net8.0
```

Expected: `The template "xUnit Test Project" was created successfully.`

- [ ] **Step 4: Add both projects to the solution and add the project reference**

```powershell
dotnet sln WhatDo.sln add src/WhatDo.StatusService/WhatDo.StatusService.csproj
dotnet sln WhatDo.sln add src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
dotnet add src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj reference src/WhatDo.StatusService/WhatDo.StatusService.csproj
```

Expected: each command prints `Project ... added to the solution.` or `Reference ... added to the project.`

- [ ] **Step 5: Add the WebApplicationFactory testing package (needed later for integration tests)**

```powershell
dotnet add src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj package Microsoft.AspNetCore.Mvc.Testing
```

Expected: `info : PackageReference for package 'Microsoft.AspNetCore.Mvc.Testing' ... added to file ...`

- [ ] **Step 6: Build to verify everything compiles**

```powershell
dotnet build WhatDo.sln
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Scaffold WhatDo.StatusService Blazor app and test project"
```

---

### Task 2: Status enum, hook payload model, and StatusMapper (TDD)

**Files:**
- Create: `src/WhatDo.StatusService/Status/HookEventStatus.cs`
- Create: `src/WhatDo.StatusService/Status/HookEventPayload.cs`
- Create: `src/WhatDo.StatusService/Status/StatusMapper.cs`
- Test: `src/WhatDo.StatusService.Tests/StatusMapperTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/WhatDo.StatusService.Tests/StatusMapperTests.cs`:

```csharp
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class StatusMapperTests
{
    [Theory]
    [InlineData("SessionStart", HookEventStatus.Idle)]
    [InlineData("UserPromptSubmit", HookEventStatus.Working)]
    [InlineData("PreToolUse", HookEventStatus.Working)]
    [InlineData("PostToolUse", HookEventStatus.Working)]
    [InlineData("PostToolUseFailure", HookEventStatus.Working)]
    [InlineData("Stop", HookEventStatus.Idle)]
    public void Map_SimpleEvents_ReturnsExpectedStatus(string hookEventName, HookEventStatus expected)
    {
        var payload = new HookEventPayload { HookEventName = hookEventName };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.UpdateStatus, result.Action);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Map_SessionEnd_ReturnsRemoveInstance()
    {
        var payload = new HookEventPayload { HookEventName = "SessionEnd" };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.RemoveInstance, result.Action);
        Assert.Null(result.Status);
    }

    [Theory]
    [InlineData("permission_prompt", HookEventStatus.PendingInput)]
    [InlineData("elicitation_dialog", HookEventStatus.PendingInput)]
    [InlineData("agent_idle", HookEventStatus.Idle)]
    [InlineData("shell_completed", HookEventStatus.Idle)]
    [InlineData("agent_completed", HookEventStatus.Idle)]
    [InlineData("shell_detached_completed", HookEventStatus.Idle)]
    public void Map_Notification_ReturnsExpectedStatus(string notificationType, HookEventStatus expected)
    {
        var payload = new HookEventPayload { HookEventName = "Notification", NotificationType = notificationType };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.UpdateStatus, result.Action);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Map_UnknownNotificationType_ReturnsIgnore()
    {
        var payload = new HookEventPayload { HookEventName = "Notification", NotificationType = "something_new" };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.Ignore, result.Action);
    }

    [Fact]
    public void Map_UnknownEventName_ReturnsIgnore()
    {
        var payload = new HookEventPayload { HookEventName = "SomethingUnexpected" };

        var result = StatusMapper.Map(payload);

        Assert.Equal(StatusAction.Ignore, result.Action);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: build FAILS with errors like `The type or namespace name 'HookEventStatus' could not be found` / `The type or namespace name 'Status' does not exist`.

- [ ] **Step 3: Create the status enum**

Create `src/WhatDo.StatusService/Status/HookEventStatus.cs`:

```csharp
namespace WhatDo.StatusService.Status;

public enum HookEventStatus
{
    Idle,
    Working,
    PendingInput
}
```

- [ ] **Step 4: Create the hook payload model**

Create `src/WhatDo.StatusService/Status/HookEventPayload.cs`:

```csharp
using System.Text.Json.Serialization;

namespace WhatDo.StatusService.Status;

/// <summary>
/// Represents the JSON body Copilot CLI POSTs to a hook's HTTP endpoint,
/// using the VS Code-compatible (PascalCase event name, snake_case fields) format.
/// </summary>
public sealed record HookEventPayload
{
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; init; }
}
```

- [ ] **Step 5: Create the StatusMapper**

Create `src/WhatDo.StatusService/Status/StatusMapper.cs`:

```csharp
namespace WhatDo.StatusService.Status;

public enum StatusAction
{
    UpdateStatus,
    RemoveInstance,
    Ignore
}

public sealed record StatusMapResult(StatusAction Action, HookEventStatus? Status = null)
{
    public static readonly StatusMapResult Ignored = new(StatusAction.Ignore);
    public static readonly StatusMapResult Removed = new(StatusAction.RemoveInstance);

    public static StatusMapResult Update(HookEventStatus status) => new(StatusAction.UpdateStatus, status);
}

public static class StatusMapper
{
    public static StatusMapResult Map(HookEventPayload payload) => payload.HookEventName switch
    {
        "SessionStart" => StatusMapResult.Update(HookEventStatus.Idle),
        "UserPromptSubmit" => StatusMapResult.Update(HookEventStatus.Working),
        "PreToolUse" => StatusMapResult.Update(HookEventStatus.Working),
        "PostToolUse" => StatusMapResult.Update(HookEventStatus.Working),
        "PostToolUseFailure" => StatusMapResult.Update(HookEventStatus.Working),
        "Stop" => StatusMapResult.Update(HookEventStatus.Idle),
        "SessionEnd" => StatusMapResult.Removed,
        "Notification" => MapNotification(payload.NotificationType),
        _ => StatusMapResult.Ignored
    };

    private static StatusMapResult MapNotification(string? notificationType) => notificationType switch
    {
        "permission_prompt" or "elicitation_dialog" => StatusMapResult.Update(HookEventStatus.PendingInput),
        "agent_idle" or "shell_completed" or "agent_completed" or "shell_detached_completed"
            => StatusMapResult.Update(HookEventStatus.Idle),
        _ => StatusMapResult.Ignored
    };
}
```

- [ ] **Step 6: Run tests to verify they pass**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: `Passed!` with all `StatusMapperTests` tests passing.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Add HookEventStatus, HookEventPayload, and StatusMapper with tests"
```

---

### Task 3: Instance ID factory (TDD)

**Files:**
- Create: `src/WhatDo.StatusService/Status/InstanceIdFactory.cs`
- Test: `src/WhatDo.StatusService.Tests/InstanceIdFactoryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/WhatDo.StatusService.Tests/InstanceIdFactoryTests.cs`:

```csharp
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class InstanceIdFactoryTests
{
    [Theory]
    [InlineData(@"C:\Users\me\source\repos\whatdo", "whatdo")]
    [InlineData("/home/user/My Cool Repo", "my-cool-repo")]
    [InlineData(@"C:\repos\whatdo\", "whatdo")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void CreateName_ReturnsSanitizedFolderName(string? cwd, string expected)
    {
        var name = InstanceIdFactory.CreateName(cwd);

        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("a1b2c3d4e5f6", "a1b2c3d4")]
    [InlineData("short", "short")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void CreateShortSessionId_TruncatesToEightChars(string? sessionId, string expected)
    {
        var shortId = InstanceIdFactory.CreateShortSessionId(sessionId);

        Assert.Equal(expected, shortId);
    }

    [Fact]
    public void CreateInstanceId_CombinesNameAndShortSessionId()
    {
        var instanceId = InstanceIdFactory.CreateInstanceId(@"C:\repos\whatdo", "a1b2c3d4e5f6");

        Assert.Equal("whatdo-a1b2c3d4", instanceId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: FAIL — `The type or namespace name 'InstanceIdFactory' could not be found`.

- [ ] **Step 3: Implement InstanceIdFactory**

Create `src/WhatDo.StatusService/Status/InstanceIdFactory.cs`:

```csharp
using System.Text.RegularExpressions;

namespace WhatDo.StatusService.Status;

public static class InstanceIdFactory
{
    public static string CreateName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "unknown";
        }

        var trimmed = cwd.TrimEnd('/', '\\');
        var folder = trimmed
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(folder))
        {
            return "unknown";
        }

        var sanitized = Regex.Replace(folder.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    public static string CreateShortSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return "unknown";
        }

        return sessionId.Length <= 8 ? sessionId : sessionId[..8];
    }

    public static string CreateInstanceId(string? cwd, string? sessionId)
        => $"{CreateName(cwd)}-{CreateShortSessionId(sessionId)}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: `Passed!` — all `InstanceIdFactoryTests` pass, plus the previous `StatusMapperTests` still pass.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "Add InstanceIdFactory with tests"
```

---

### Task 4: In-memory instance store (TDD)

**Files:**
- Create: `src/WhatDo.StatusService/Status/InstanceStatusInfo.cs`
- Create: `src/WhatDo.StatusService/Status/IInstanceStore.cs`
- Create: `src/WhatDo.StatusService/Status/InMemoryInstanceStore.cs`
- Test: `src/WhatDo.StatusService.Tests/InMemoryInstanceStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/WhatDo.StatusService.Tests/InMemoryInstanceStoreTests.cs`:

```csharp
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class InMemoryInstanceStoreTests
{
    [Fact]
    public void Upsert_NewInstance_AppearsInGetAll()
    {
        var store = new InMemoryInstanceStore();

        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("whatdo-a1b2c3d4", all[0].InstanceId);
        Assert.Equal("whatdo", all[0].Name);
        Assert.Equal(HookEventStatus.Working, all[0].Status);
        Assert.Equal("UserPromptSubmit", all[0].LastEvent);
    }

    [Fact]
    public void Upsert_ExistingInstance_UpdatesStatusInPlace()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Idle, "Stop");

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal(HookEventStatus.Idle, all[0].Status);
        Assert.Equal("Stop", all[0].LastEvent);
    }

    [Fact]
    public void Remove_ExistingInstance_RemovesIt()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        store.Remove("whatdo-a1b2c3d4");

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Remove_UnknownInstance_DoesNotThrow()
    {
        var store = new InMemoryInstanceStore();

        var exception = Record.Exception(() => store.Remove("does-not-exist"));

        Assert.Null(exception);
    }

    [Fact]
    public void GetAll_MultipleInstances_ReturnsAll()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");
        store.Upsert("other-e5f6a7b8", "other", @"C:\repos\other", HookEventStatus.Idle, "Stop");

        var all = store.GetAll();

        Assert.Equal(2, all.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: FAIL — `The type or namespace name 'InMemoryInstanceStore' could not be found`.

- [ ] **Step 3: Create InstanceStatusInfo**

Create `src/WhatDo.StatusService/Status/InstanceStatusInfo.cs`:

```csharp
namespace WhatDo.StatusService.Status;

public sealed record InstanceStatusInfo(
    string InstanceId,
    string Name,
    string Cwd,
    HookEventStatus Status,
    string LastEvent,
    DateTimeOffset LastSeenUtc);
```

- [ ] **Step 4: Create IInstanceStore**

Create `src/WhatDo.StatusService/Status/IInstanceStore.cs`:

```csharp
namespace WhatDo.StatusService.Status;

public interface IInstanceStore
{
    void Upsert(string instanceId, string name, string cwd, HookEventStatus status, string lastEvent);

    void Remove(string instanceId);

    IReadOnlyList<InstanceStatusInfo> GetAll();
}
```

- [ ] **Step 5: Create InMemoryInstanceStore**

Create `src/WhatDo.StatusService/Status/InMemoryInstanceStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace WhatDo.StatusService.Status;

public sealed class InMemoryInstanceStore : IInstanceStore
{
    private readonly ConcurrentDictionary<string, InstanceStatusInfo> _instances = new();

    public void Upsert(string instanceId, string name, string cwd, HookEventStatus status, string lastEvent)
    {
        _instances[instanceId] = new InstanceStatusInfo(
            instanceId,
            name,
            cwd,
            status,
            lastEvent,
            DateTimeOffset.UtcNow);
    }

    public void Remove(string instanceId)
    {
        _instances.TryRemove(instanceId, out _);
    }

    public IReadOnlyList<InstanceStatusInfo> GetAll()
        => _instances.Values.ToList();
}
```

- [ ] **Step 6: Run tests to verify they pass**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: `Passed!` — all tests across the three test classes pass.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Add InstanceStatusInfo, IInstanceStore, and InMemoryInstanceStore with tests"
```

---

### Task 5: Ingest endpoint (`POST /api/status-event`) with integration tests

**Files:**
- Create: `src/WhatDo.StatusService/Endpoints/StatusEventEndpoints.cs`
- Modify: `src/WhatDo.StatusService/Program.cs`
- Modify: `src/WhatDo.StatusService/appsettings.Development.json` (add a secret entry)
- Test: `src/WhatDo.StatusService.Tests/StatusEventEndpointTests.cs`

- [ ] **Step 1: Write the endpoint mapping extension**

Create `src/WhatDo.StatusService/Endpoints/StatusEventEndpoints.cs`:

```csharp
using System.Text.Json;
using WhatDo.StatusService.Status;

namespace WhatDo.StatusService.Endpoints;

public static class StatusEventEndpoints
{
    public const string SecretHeaderName = "X-Status-Secret";

    public static void MapStatusEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/status-event", async (HttpRequest request, IInstanceStore store, IConfiguration config) =>
        {
            var expectedSecret = config["StatusIngest:Secret"];
            if (!string.IsNullOrEmpty(expectedSecret))
            {
                var providedSecret = request.Headers[SecretHeaderName].ToString();
                if (!string.Equals(providedSecret, expectedSecret, StringComparison.Ordinal))
                {
                    return Results.Unauthorized();
                }
            }

            HookEventPayload? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<HookEventPayload>(request.Body);
            }
            catch (JsonException)
            {
                return Results.Ok(); // malformed payload: log-and-ignore per design, never fail the CLI hook
            }

            if (payload is null)
            {
                return Results.Ok();
            }

            var instanceId = InstanceIdFactory.CreateInstanceId(payload.Cwd, payload.SessionId);
            var name = InstanceIdFactory.CreateName(payload.Cwd);
            var result = StatusMapper.Map(payload);

            switch (result.Action)
            {
                case StatusAction.UpdateStatus:
                    store.Upsert(instanceId, name, payload.Cwd ?? "", result.Status!.Value, payload.HookEventName ?? "");
                    break;
                case StatusAction.RemoveInstance:
                    store.Remove(instanceId);
                    break;
                case StatusAction.Ignore:
                default:
                    break;
            }

            return Results.Ok();
        });
    }
}
```

- [ ] **Step 2: Register the store and map the endpoint in Program.cs**

Replace the full contents of `src/WhatDo.StatusService/Program.cs` with:

```csharp
using WhatDo.StatusService.Components;
using WhatDo.StatusService.Endpoints;
using WhatDo.StatusService.Status;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IInstanceStore, InMemoryInstanceStore>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStatusEventEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program
{
    // Marker partial class so WebApplicationFactory<Program> can find this entry point in tests.
}
```

- [ ] **Step 3: Add a development secret to appsettings.Development.json**

Modify `src/WhatDo.StatusService/appsettings.Development.json` to look like:

```json
{
  "DetailedErrors": true,
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "StatusIngest": {
    "Secret": "dev-secret-change-me"
  }
}
```

- [ ] **Step 4: Write the integration tests**

Create `src/WhatDo.StatusService.Tests/StatusEventEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class StatusEventEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StatusEventEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostStatusEvent_WithoutSecret_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "a1b2c3d4e5f6",
            cwd = @"C:\repos\whatdo"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostStatusEvent_WithCorrectSecret_UpdatesStore()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        var response = await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "a1b2c3d4e5f6",
            cwd = @"C:\repos\whatdo"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInstanceStore>();
        var all = store.GetAll();
        Assert.Contains(all, i => i.InstanceId == "whatdo-a1b2c3d4" && i.Status == HookEventStatus.Working);
    }

    [Fact]
    public async Task PostStatusEvent_SessionEndAfterSessionStart_RemovesInstance()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "SessionStart",
            session_id = "deadbeef1234",
            cwd = @"C:\repos\whatdo"
        });

        var response = await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "SessionEnd",
            session_id = "deadbeef1234",
            cwd = @"C:\repos\whatdo"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInstanceStore>();
        Assert.DoesNotContain(store.GetAll(), i => i.InstanceId == "whatdo-deadbeef");
    }

    [Fact]
    public async Task PostStatusEvent_MalformedJson_ReturnsOkAndDoesNotThrow()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        var response = await client.PostAsync("/api/status-event",
            new StringContent("{not valid json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 5: Note on Development configuration**

The `appsettings.Development.json` secret must be picked up by `WebApplicationFactory` for the "with correct secret" test to pass. `WebApplicationFactory<Program>` defaults `ASPNETCORE_ENVIRONMENT` to `"Development"` automatically, so no extra config is needed — this step is just a note confirming why Step 3's file matters. No file changes here.

- [ ] **Step 6: Run all tests**

```powershell
dotnet test src/WhatDo.StatusService.Tests/WhatDo.StatusService.Tests.csproj
```

Expected: `Passed!` — all tests pass, including the 4 new `StatusEventEndpointTests`.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Add status-event ingest endpoint with secret validation and integration tests"
```

---

### Task 6: Dashboard page shows live status

**Files:**
- Modify: `src/WhatDo.StatusService/Components/Pages/Home.razor`
- Modify: `src/WhatDo.StatusService/Components/_Imports.razor`

- [ ] **Step 1: Add the Status namespace to shared imports**

Modify `src/WhatDo.StatusService/Components/_Imports.razor` by appending this line at the end:

```razor
@using WhatDo.StatusService.Status
```

- [ ] **Step 2: Replace Home.razor with the live dashboard**

Replace the full contents of `src/WhatDo.StatusService/Components/Pages/Home.razor` with:

```razor
@page "/"
@rendermode InteractiveServer
@inject IInstanceStore Store
@implements IAsyncDisposable

<PageTitle>WhatDo — Copilot CLI Status</PageTitle>

<h1>Copilot CLI Status</h1>

@if (_instances.Count == 0)
{
    <p><em>No active Copilot CLI instances reporting.</em></p>
}
else
{
    <table class="table">
        <thead>
            <tr>
                <th>Name</th>
                <th>Status</th>
                <th>Last Event</th>
                <th>Last Seen (UTC)</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var instance in _instances)
            {
                <tr>
                    <td>@instance.Name</td>
                    <td>@instance.Status</td>
                    <td>@instance.LastEvent</td>
                    <td>@instance.LastSeenUtc.ToString("HH:mm:ss")</td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private List<InstanceStatusInfo> _instances = new();
    private PeriodicTimer? _timer;

    protected override void OnInitialized()
    {
        _instances = Store.GetAll().OrderBy(i => i.Name).ToList();
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            _ = PollAsync();
        }

        return Task.CompletedTask;
    }

    private async Task PollAsync()
    {
        while (_timer is not null && await _timer.WaitForNextTickAsync())
        {
            _instances = Store.GetAll().OrderBy(i => i.Name).ToList();
            await InvokeAsync(StateHasChanged);
        }
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Build to confirm the Razor component compiles**

```powershell
dotnet build src/WhatDo.StatusService/WhatDo.StatusService.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "Show live Copilot CLI instance status on the dashboard home page"
```

---

### Task 7: Copilot CLI hook config template + manual end-to-end verification

**Files:**
- Create: `hooks/whatdo-status.json`
- Create: `hooks/README.md`

- [ ] **Step 1: Run the app and note its URL**

```powershell
dotnet run --project src/WhatDo.StatusService/WhatDo.StatusService.csproj
```

Expected: console output includes a line like `Now listening on: http://localhost:PORT` — note the exact `http://localhost:PORT` value printed (the port is randomly assigned by the template and will vary).

- [ ] **Step 2: Create the hook config template**

Create `hooks/whatdo-status.json` (replace `http://localhost:PORT` with the actual URL noted in Step 1 when you install this locally — this file documents the shape for later real use):

```json
{
  "version": 1,
  "hooks": {
    "SessionStart": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ],
    "UserPromptSubmit": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ],
    "PreToolUse": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ],
    "PostToolUse": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ],
    "PostToolUseFailure": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ],
    "Notification": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ],
    "Stop": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ],
    "SessionEnd": [
      {
        "type": "http",
        "url": "http://localhost:PORT/api/status-event",
        "headers": { "X-Status-Secret": "dev-secret-change-me" }
      }
    ]
  }
}
```

> Note: `http://` is only accepted by Copilot CLI HTTP hooks for `localhost`/`127.*`/`[::1]`, and only when `COPILOT_HOOK_ALLOW_LOCALHOST=1` is set in the environment running the CLI. Once this moves to a deployed Azure URL in a later stage, switch to `https://` and this env var will no longer be required.

- [ ] **Step 3: Create hooks/README.md documenting manual verification steps**

Create `hooks/README.md`:

```markdown
# WhatDo status hooks (Stage 1 — local manual testing)

## One-time setup

1. Run the app: `dotnet run --project src/WhatDo.StatusService/WhatDo.StatusService.csproj`
2. Note the `http://localhost:PORT` URL from the console output.
3. Copy `hooks/whatdo-status.json` into `~/.copilot/hooks/whatdo-status.json` (or
   `%USERPROFILE%\.copilot\hooks\whatdo-status.json` on Windows), replacing every
   `http://localhost:PORT` with the actual port from step 2.
4. Set `COPILOT_HOOK_ALLOW_LOCALHOST=1` in the environment before launching `copilot`
   (required for non-TLS localhost HTTP hooks).

## Manual verification without the CLI (fastest check)

With the app running, POST a sample event directly:

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:PORT/api/status-event" `
  -Headers @{ "X-Status-Secret" = "dev-secret-change-me" } `
  -ContentType "application/json" `
  -Body '{"hook_event_name":"UserPromptSubmit","session_id":"a1b2c3d4e5f6","cwd":"C:\\repos\\whatdo"}'
```

Open `http://localhost:PORT/` in a browser — within 2 seconds you should see a row:
`whatdo | Working | UserPromptSubmit | <time>`.

Then POST a `Stop` event for the same session and confirm the row updates to `Idle`,
and a `SessionEnd` event and confirm the row disappears.

## Manual verification with the real Copilot CLI

1. With the hook file installed (see setup above) and the app running, start a new
   `copilot` session in any repo.
2. Watch the dashboard page — a row should appear shortly after session start (`Idle`).
3. Submit a prompt — the row should flip to `Working`.
4. When Copilot asks for a tool permission, the row should flip to `PendingInput`.
5. When the turn finishes, the row should return to `Idle`.
6. Exit the CLI session (`/exit` or `ctrl+d`) — the row should disappear.
```

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "Add Copilot CLI hook config template and manual verification instructions"
```

---

## STOP — Stage 1 Manual Verification Checkpoint

Do not proceed to Stage 2 (persistence) until you have manually verified Stage 1 using the steps in `hooks/README.md`:

1. `dotnet run` starts the app without errors.
2. A direct `Invoke-RestMethod` POST updates the dashboard within ~2 seconds.
3. A `SessionEnd` POST removes the row from the dashboard.
4. (Optional but recommended) A real Copilot CLI session, with the hook file installed, drives the dashboard through `Idle → Working → PendingInput → Idle → (removed)` as described above.

Once you've confirmed this works, tell me and I'll write the Stage 2 plan (Azure Table Storage persistence, replacing `InMemoryInstanceStore`).

## Roadmap (future stages — not yet planned in detail)

- **Stage 2:** Persist instance state in Azure Table Storage instead of in-memory, so state survives app restarts.
- **Stage 3:** Add MQTT publishing (HiveMQ Cloud) from the ingest endpoint, plus the 60s staleness sweep for crashed sessions.
- **Stage 4:** Switch the dashboard to subscribe to MQTT (via the background MQTTnet client) instead of polling the local store directly.
- **Stage 5:** ESP32 firmware reference sketch subscribing directly to HiveMQ.
- **Stage 6:** Azure deployment (App Service Free tier), real shared-secret configuration, HTTPS hook config pointed at the deployed URL.

Each stage will get its own plan document, written after the prior stage is manually verified, per the design spec at `docs/superpowers/specs/2026-09-21-copilot-cli-status-reporting-design.md`.
