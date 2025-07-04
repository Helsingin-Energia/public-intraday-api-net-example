using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using NPS.ID.PublicApi.Client.Connection.Enums;
using NPS.ID.PublicApi.Client.Connection.Events;
using NPS.ID.PublicApi.Client.Connection.Exceptions;
using NPS.ID.PublicApi.Client.Connection.Messages;
using NPS.ID.PublicApi.Client.Connection.Subscriptions;
using NPS.ID.PublicApi.Client.Connection.Subscriptions.Exceptions;
using NPS.ID.PublicApi.Client.Connection.Subscriptions.Requests;
using NPS.ID.PublicApi.Client.Connection.Extensions;
using NPS.ID.PublicApi.Client.Connection.Options;
using NPS.ID.PublicApi.Client.Security.Options;

namespace NPS.ID.PublicApi.Client.Connection.Clients;

public class StompClient : IClient
{
    private bool _connectionClosed;

    private readonly AsyncManualResetEvent _connectionEstablishedEvent = new();
    private readonly AsyncManualResetEvent _connectionClosedEvent = new();

    private readonly ILogger<StompClient> _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly WebSocketConnector _webSocketConnector;

    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    public WebSocketClientTarget ClientTarget { get; }
    public string ClientId { get; }

    public event EventHandler ConnectionEstablished;
    public event EventHandler ConnectionClosed;
    public event EventHandler StompError;

    public StompClient(
        ILogger<StompClient> logger,
        ILoggerFactory loggerFactory,
        WebSocketConnectorFactory webSocketConnectorFactory,
        WebSocketClientTarget target,
        string clientId,
        WebSocketOptions webSocketOptions,
        CredentialsOptions credentialsOptions)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _webSocketConnector = webSocketConnectorFactory
            .Create(webSocketOptions, credentialsOptions, OnMessageReceivedAsync, OnConnectionEstablishedAsync, OnConnectionClosedAsync, OnStompErrorAsync);
        ClientTarget = target;
        ClientId = clientId;
    }

    private Task OnConnectionEstablishedAsync()
    {
        _connectionEstablishedEvent.Set();
        ConnectionEstablished?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("[{ClientTarget}] Connection established for client {ClientId}", ClientTarget, ClientId);

        return Task.CompletedTask;
    }

    private Task OnConnectionClosedAsync()
    {
        _connectionClosedEvent.Set();
        ConnectionClosed?.Invoke(this, EventArgs.Empty);
        if (_connectionClosed)
        {
            _logger.LogInformation("[{ClientTarget}] Connection closing for client {ClientId}", ClientTarget, ClientId);
        }
        else
        {
            _logger.LogError("[{ClientTarget}] Connection closed unexpectedly for client {ClientId}", ClientTarget, ClientId);
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Close();
        }

        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(MessageReceivedEventArgs e, CancellationToken cancellationToken)
    {
        var isMessage = e.Message.IsMessageCommand();
        if (!isMessage)
        {
            return Task.CompletedTask;
        }

        var stompFrame = e.Message.ConvertToStompFrame();

        if (stompFrame.Properties.TryGetValue(Headers.Server.Subscription, out var subscriptionId))
        {
            _logger.LogInformation("[{ClientTarget}][Frame({SubscriptionId}):Metadata] destination={Destination}, sentAt={SentAt}, snapshot={Snapshot}, publishingMode={PublishingMode}, sequenceNo={SequenceNo}",
                ClientTarget,
                subscriptionId,
                stompFrame.GetDestination(),
                stompFrame.GetSentAtTimestamp(),
                stompFrame.IsSnapshot(),
                stompFrame.GetPublishingMode(),
                stompFrame.GetSequenceNumber());

            if (!_subscriptions.TryGetValue(subscriptionId, out var targetSubscription))
            {
                _logger.LogWarning("[{ClientTarget}][Frame({SubscriptionId})] Received message for subscription that is not assigned to current client", ClientTarget, subscriptionId);
                return Task.CompletedTask;
            }

            if (targetSubscription.SequenceNumber != 0 && targetSubscription.SequenceNumber + 1 != stompFrame.GetSequenceNumber())
            {
                _logger.LogError("[{ClientTarget}][Frame({SubscriptionId})] Sequence number mismatch! Expected: {Expected}, Received: {Received}",
                    ClientTarget,
                    subscriptionId,
                    targetSubscription.SequenceNumber + 1,
                    stompFrame.GetSequenceNumber());

                StompError?.Invoke(this, EventArgs.Empty);
            }

            targetSubscription.SequenceNumber = stompFrame.GetSequenceNumber();
            targetSubscription.OnMessage(stompFrame, e.Timestamp);
        }
        else
        {
            _logger.LogWarning("[{ClientTarget}] Unrecognized message received from {StompConnectionUri}. Command:{Command}\nHeaders:\n{Headers}\n{Content}",
                ClientTarget,
                _webSocketConnector.ConnectionUri,
                stompFrame.Command,
                string.Join('\n', stompFrame.Properties.Select(header => $"{header.Key}:{header.Value}")),
                Encoding.UTF8.GetString(stompFrame.Content));
        }

        return Task.CompletedTask;
    }

    private Task OnStompErrorAsync(StompConnectionException exception)
    {
        _logger.LogError(exception, "[{ClientTarget}] An error on web socket message processing", ClientTarget);
        StompError?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task<bool> OpenAsync(CancellationToken cancellationToken)
    {
        if (_webSocketConnector.IsConnected)
        {
            return true;
        }

        await _webSocketConnector.ConnectAsync(cancellationToken);

        await _connectionEstablishedEvent.WaitAsync(cancellationToken);
        return _webSocketConnector.IsConnected;
    }

    public async Task<ISubscription<TValue>> SubscribeAsync<TValue>(SubscribeRequest request, CancellationToken cancellationToken)
    {
        if (!_webSocketConnector.IsConnected)
        {
            throw new SubscriptionFailedException(
                $"[{ClientTarget}][Destination:{request.Destination}] Failed to subscribe because no connection is established! Connect first!");
        }

        var subscription = new StompSubscription<TValue>(
            request.SubscriptionId,
            request.Type,
            request.Destination,
            _loggerFactory.CreateLogger<StompSubscription<TValue>>(),
            Channel.CreateBounded<ReceivedMessage<IReadOnlyCollection<TValue>>>(new BoundedChannelOptions(30_000)
            {
                FullMode = BoundedChannelFullMode.Wait
            }));

        _subscriptions[subscription.Id] = subscription;
        var subscribeFrame = StompMessageFactory.SubscribeFrame(request.Destination, request.SubscriptionId);
        await _webSocketConnector.SendStompFrameAsync(subscribeFrame, cancellationToken);
        return subscription;
    }

    public async Task SendAsync<TRequest>(TRequest request, string destination, CancellationToken cancellationToken)
        where TRequest : class, new()
    {
        var payload = JsonSerializer.Serialize(request);
        var payloadFrame = StompMessageFactory.SendFrame(payload, destination);
        await _webSocketConnector.SendStompFrameAsync(payloadFrame, cancellationToken);
    }

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        var unsubscribeFrame = StompMessageFactory.Unsubscribe(subscriptionId);
        await _webSocketConnector.SendStompFrameAsync(unsubscribeFrame, cancellationToken);

        if (_subscriptions.Remove(subscriptionId, out var subscription))
        {
            subscription.Close();

            _logger.LogInformation("[{ClientTarget}][SubscriptionId:{Subscription}] Unsubscribed", ClientTarget, subscription.Id);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await UnsubscribeAllAsync(cancellationToken);
        _connectionClosed = true;
        await _webSocketConnector.DisposeAsync();
    }

    private async Task UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        if (!_webSocketConnector.IsConnected)
        {
            return;
        }

        var subscriptions = _subscriptions
            .Values
            .ToList();

        foreach (var subscription in subscriptions)
        {
            await UnsubscribeAsync(subscription.Id, cancellationToken);
        }
    }
}