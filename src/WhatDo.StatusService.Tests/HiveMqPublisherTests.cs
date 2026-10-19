using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using WhatDo.StatusService.Mqtt;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class HiveMqPublisherTests
{
    [Fact]
    public async Task PublishAggregateAsync_WithClient_PublishesRetainedAggregateMessage()
    {
        var client = new RecordingMqttClient();
        var publisher = CreatePublisher(client, new MqttOptions { Host = "broker.example.com" });

        await publisher.PublishAggregateAsync(new AggregateStatusMessage("working", 1, 2, 3, 6));

        var message = Assert.Single(client.Messages);
        Assert.Equal(MqttTopics.AggregateTopic, message.Topic);
        Assert.True(message.Retain);
        Assert.Equal("""{"status":"working","workingCount":1,"idleCount":2,"pendingCount":3,"total":6}""", ReadPayload(message));
    }

    [Fact]
    public async Task ClearInstanceStatusAsync_WithClient_PublishesEmptyRetainedMessage()
    {
        var client = new RecordingMqttClient();
        var publisher = CreatePublisher(client, new MqttOptions { Host = "broker.example.com" });

        await publisher.ClearInstanceStatusAsync("my-repo-a1b2c3d4");

        var message = Assert.Single(client.Messages);
        Assert.Equal("whatdo/status/my-repo/a1b2c3d4", message.Topic);
        Assert.True(message.Retain);
        Assert.Equal(string.Empty, ReadPayload(message));
    }

    [Fact]
    public async Task PublishInstanceStatusAsync_WhenClientThrows_DoesNotThrow()
    {
        var publisher = CreatePublisher(new ThrowingMqttClient(), new MqttOptions { Host = "broker.example.com" });
        var message = new InstanceStatusMessage("idle", "whatdo", @"C:\repos\whatdo", "Stop", DateTimeOffset.Parse("2026-10-03T19:12:29+00:00"));

        await publisher.PublishInstanceStatusAsync("whatdo-a1b2c3d4", message);
    }

    [Fact]
    public async Task PublishInstanceStatusAsync_WhenMqttIsNotConfigured_IsNoOp()
    {
        var client = new RecordingMqttClient();
        var publisher = CreatePublisher(client, new MqttOptions());
        var message = new InstanceStatusMessage("idle", "whatdo", @"C:\repos\whatdo", "Stop", DateTimeOffset.UtcNow);

        await publisher.PublishInstanceStatusAsync("whatdo-a1b2c3d4", message);

        Assert.Empty(client.Messages);
    }

    private static HiveMqPublisher CreatePublisher(IMqttClient? client, MqttOptions options)
        => new(new StubAccessor(client), Options.Create(options), NullLogger<HiveMqPublisher>.Instance);

    private static string ReadPayload(MqttApplicationMessage message)
    {
        if (message.Payload.IsEmpty)
        {
            return string.Empty;
        }

        using var stream = new MemoryStream();
        foreach (var segment in message.Payload)
        {
            stream.Write(segment.Span);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class StubAccessor(IMqttClient? client) : IManagedMqttClientAccessor
    {
        public IMqttClient? Client { get; } = client;
    }

    private sealed class RecordingMqttClient : IMqttClient
    {
        public List<MqttApplicationMessage> Messages { get; } = [];

        public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;
        public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;
        public event Func<MqttClientConnectingEventArgs, Task>? ConnectingAsync;
        public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;
        public event Func<MQTTnet.Diagnostics.PacketInspection.InspectMqttPacketEventArgs, Task>? InspectPacketAsync;

        public bool IsConnected => true;

        public MqttClientOptions Options => throw new NotSupportedException();

        public Task<MqttClientConnectResult> ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DisconnectAsync(MqttClientDisconnectOptions options = null!, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MqttClientPublishResult> PublishAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
        {
            _ = ApplicationMessageReceivedAsync;
            _ = ConnectedAsync;
            _ = ConnectingAsync;
            _ = DisconnectedAsync;
            _ = InspectPacketAsync;

            Messages.Add(applicationMessage);
            return Task.FromResult((MqttClientPublishResult)null!);
        }

        public Task SendEnhancedAuthenticationExchangeDataAsync(MqttEnhancedAuthenticationExchangeData data, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MqttClientSubscribeResult> SubscribeAsync(MqttClientSubscribeOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MqttClientUnsubscribeResult> UnsubscribeAsync(MqttClientUnsubscribeOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task PingAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingMqttClient : IMqttClient
    {
        public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;
        public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;
        public event Func<MqttClientConnectingEventArgs, Task>? ConnectingAsync;
        public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;
        public event Func<MQTTnet.Diagnostics.PacketInspection.InspectMqttPacketEventArgs, Task>? InspectPacketAsync;

        public bool IsConnected => true;

        public MqttClientOptions Options => throw new NotSupportedException();

        public Task<MqttClientConnectResult> ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DisconnectAsync(MqttClientDisconnectOptions options = null!, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MqttClientPublishResult> PublishAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
        {
            _ = ApplicationMessageReceivedAsync;
            _ = ConnectedAsync;
            _ = ConnectingAsync;
            _ = DisconnectedAsync;
            _ = InspectPacketAsync;

            throw new InvalidOperationException("boom");
        }

        public Task SendEnhancedAuthenticationExchangeDataAsync(MqttEnhancedAuthenticationExchangeData data, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MqttClientSubscribeResult> SubscribeAsync(MqttClientSubscribeOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MqttClientUnsubscribeResult> UnsubscribeAsync(MqttClientUnsubscribeOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task PingAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
