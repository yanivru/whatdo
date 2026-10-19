using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatDo.StatusService.Mqtt;

namespace WhatDo.StatusService.Tests;

public class MqttConnectionServiceTests
{
    [Fact]
    public async Task StartAsync_WhenMqttIsNotConfigured_DoesNotCreateClient()
    {
        var service = new MqttConnectionService(
            Options.Create(new MqttOptions()),
            NullLogger<MqttConnectionService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Null(service.Client);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ProgramServices_RegisterSingleMqttConnectionServiceInstanceForHostedServiceAndAccessor()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IManagedMqttClientAccessor>();
        var connectionService = scope.ServiceProvider.GetRequiredService<MqttConnectionService>();
        var hostedServices = scope.ServiceProvider.GetServices<IHostedService>();

        Assert.Same(connectionService, accessor);
        Assert.Contains(connectionService, hostedServices);
    }
}
