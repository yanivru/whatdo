using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace WhatDo.StatusService.Mqtt;

public sealed class MqttConnectionService(
    IOptions<MqttOptions> options,
    ILogger<MqttConnectionService> logger) : BackgroundService, IManagedMqttClientAccessor
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private const int MaxClientIdLength = 48;

    private readonly MqttOptions _options = options.Value;
    private readonly ILogger<MqttConnectionService> _logger = logger;
    private readonly string _clientId = CreateClientId(options.Value.ClientIdPrefix);

    private IMqttClient? _client;
    private TaskCompletionSource _disconnectSignal = CreateDisconnectSignal();

    public IMqttClient? Client => _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogInformation("MQTT not configured (Mqtt:Host is empty) — skipping HiveMQ connection");
            await WaitUntilStoppedAsync(stoppingToken);
            return;
        }

        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();
        _client = client;

        client.DisconnectedAsync += args =>
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Disconnected from HiveMQ broker at {Host}:{Port}: {Reason}", _options.Host, _options.Port, args.Reason);
                _disconnectSignal.TrySetResult();
            }

            return Task.CompletedTask;
        };

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _disconnectSignal = CreateDisconnectSignal();

                if (!await TryConnectAsync(client, stoppingToken))
                {
                    await DelayForReconnectAsync(stoppingToken);
                    continue;
                }

                await WaitForDisconnectAsync(stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    await DelayForReconnectAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await DisconnectAsync(client);
            client.Dispose();
            _client = null;
        }
    }

    private async Task<bool> TryConnectAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        if (client.IsConnected)
        {
            return true;
        }

        try
        {
            await client.ConnectAsync(BuildClientOptions(), cancellationToken);
            _logger.LogInformation("Connected to HiveMQ broker at {Host}:{Port}", _options.Host, _options.Port);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to HiveMQ broker at {Host}:{Port}; retrying in {DelaySeconds}s", _options.Host, _options.Port, ReconnectDelay.TotalSeconds);
            return false;
        }
    }

    private MqttClientOptions BuildClientOptions()
        => new MqttClientOptionsBuilder()
            .WithClientId(_clientId)
            .WithTcpServer(_options.Host, _options.Port)
            .WithCredentials(_options.Username, _options.Password)
            .WithTlsOptions(options => options.UseTls())
            .Build();

    private async Task WaitForDisconnectAsync(CancellationToken cancellationToken)
    {
        using var cancellationRegistration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), _disconnectSignal);
        await _disconnectSignal.Task;
    }

    private static TaskCompletionSource CreateDisconnectSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilStoppedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static async Task DelayForReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReconnectDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DisconnectAsync(IMqttClient client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            await client.DisconnectAsync(cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring MQTT disconnect error during shutdown");
        }
    }

    private static string CreateClientId(string prefix)
    {
        var trimmedPrefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim();
        var suffix = Guid.NewGuid().ToString("N");
        var maxPrefixLength = Math.Max(0, MaxClientIdLength - suffix.Length - 1);

        if (trimmedPrefix.Length > maxPrefixLength)
        {
            trimmedPrefix = trimmedPrefix[..maxPrefixLength];
        }

        return string.IsNullOrEmpty(trimmedPrefix)
            ? suffix[..MaxClientIdLength]
            : $"{trimmedPrefix}-{suffix}";
    }
}
