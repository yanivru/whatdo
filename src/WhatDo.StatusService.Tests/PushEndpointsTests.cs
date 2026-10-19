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
