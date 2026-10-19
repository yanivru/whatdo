using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WhatDo.StatusService.Mqtt;
using WhatDo.StatusService.Push;
using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class StatusEventEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeMqttPublisher _mqttPublisher = new();
    private readonly FakePushNotifier _pushNotifier = new();

    public StatusEventEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMqttPublisher>();
                services.AddSingleton<IMqttPublisher>(_mqttPublisher);
                services.RemoveAll<IPushNotifier>();
                services.AddSingleton<IPushNotifier>(_pushNotifier);
            });
        });
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

    [Fact]
    public async Task PostStatusEvent_MalformedJson_ReturnsOkAndDoesNotThrow()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        var response = await client.PostAsync("/api/status-event",
            new StringContent("{not valid json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostStatusEvent_NotificationWithCamelCaseSessionId_UpdatesExistingInstance()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Status-Secret", "dev-secret-change-me");

        await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "cafef00d1234",
            cwd = @"C:\repos\whatdo"
        });

        // The real CLI sends "sessionId" (camelCase) for Notification events, even
        // though every other field on the VS Code-compatible payload is snake_case.
        var response = await client.PostAsJsonAsync("/api/status-event", new
        {
            hook_event_name = "Notification",
            sessionId = "cafef00d1234",
            cwd = @"C:\repos\whatdo",
            notification_type = "permission_prompt"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInstanceStore>();
        var all = store.GetAll();
        Assert.Contains(all, i => i.InstanceId == "whatdo-cafef00d" && i.Status == HookEventStatus.PendingInput);
        Assert.DoesNotContain(all, i => i.InstanceId == "whatdo-");
    }

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
}
