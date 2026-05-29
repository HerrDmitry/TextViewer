using System.Threading.Channels;

namespace TextViewer.Services;

/// <summary>
/// Backend message bus host. Receives messages from the bridge,
/// routes to registered handlers, sends responses back. Sequential processing
/// via Channel guarantees no concurrent handler execution.
/// </summary>
public sealed class MessageBusHost : IDisposable
{
    private readonly IMessageBridge _bridge;
    private readonly Dictionary<string, Func<string, string, Task<string?>>> _handlers = new();
    private readonly Channel<string> _inboundChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingLoop;
    private bool _disposed;

    public MessageBusHost(IMessageBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

        _inboundChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _bridge.WebMessageReceived += OnWebMessageReceived;
        _processingLoop = Task.Run(ProcessLoopAsync);
    }

    /// <summary>
    /// Registers a handler for a given message type.
    /// Handler receives (correlationId, payload) and returns response payload or null (fire-and-forget).
    /// </summary>
    public void RegisterHandler(string messageType, Func<string, string, Task<string?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (!MessageProtocol.ValidateMessageType(messageType))
            throw new ArgumentException($"Invalid message type: '{messageType}'", nameof(messageType));

        _handlers[messageType] = handler;
    }

    /// <summary>
    /// Sends an unsolicited message to the frontend with a generated Correlation_ID.
    /// </summary>
    public void Send(string messageType, string payload)
    {
        if (!MessageProtocol.ValidateMessageType(messageType))
            throw new ArgumentException($"Invalid message type: '{messageType}'");
        if (!MessageProtocol.ValidatePayload(payload))
            throw new ArgumentException("Payload exceeds maximum length");

        var correlationId = Guid.NewGuid().ToString();
        var envelope = MessageProtocol.Encode(messageType, correlationId, payload);
        _bridge.SendWebMessage(envelope);
    }

    /// <summary>
    /// Sends a system message (must have "system:" prefix) to the frontend.
    /// </summary>
    public void SendSystemMessage(string systemType, string payload)
    {
        if (!systemType.StartsWith("system:"))
            throw new ArgumentException($"System message type must start with 'system:': '{systemType}'");
        if (!MessageProtocol.ValidateMessageType(systemType))
            throw new ArgumentException($"Invalid message type: '{systemType}'");
        if (!MessageProtocol.ValidatePayload(payload))
            throw new ArgumentException("Payload exceeds maximum length");

        var correlationId = Guid.NewGuid().ToString();
        var envelope = MessageProtocol.Encode(systemType, correlationId, payload);
        _bridge.SendWebMessage(envelope);
    }

    /// <summary>
    /// Sends a response with a specific correlationId back to the frontend.
    /// </summary>
    public void SendResponse(string messageType, string correlationId, string payload)
    {
        if (!MessageProtocol.ValidateMessageType(messageType))
            throw new ArgumentException($"Invalid message type: '{messageType}'");
        if (!MessageProtocol.ValidateCorrelationId(correlationId))
            throw new ArgumentException($"Invalid correlation ID: '{correlationId}'");
        if (!MessageProtocol.ValidatePayload(payload))
            throw new ArgumentException("Payload exceeds maximum length");

        var envelope = MessageProtocol.Encode(messageType, correlationId, payload);
        _bridge.SendWebMessage(envelope);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bridge.WebMessageReceived -= OnWebMessageReceived;
        _cts.Cancel();
        _inboundChannel.Writer.TryComplete();

        // Wait for processing loop to finish (with timeout to avoid deadlock)
        _processingLoop.Wait(TimeSpan.FromSeconds(5));

        _cts.Dispose();
    }

    private void OnWebMessageReceived(object? sender, string message)
    {
        // Enqueue for sequential processing — non-blocking
        _inboundChannel.Writer.TryWrite(message);
    }

    private async Task ProcessLoopAsync()
    {
        var reader = _inboundChannel.Reader;
        var token = _cts.Token;

        try
        {
            await foreach (var rawMessage in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await ProcessMessageAsync(rawMessage).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task ProcessMessageAsync(string rawMessage)
    {
        var decoded = MessageProtocol.Decode(rawMessage);
        if (decoded is null)
        {
            Console.Error.WriteLine("[MessageBusHost] Warning: Failed to parse message envelope, discarding.");
            return;
        }

        var (messageType, correlationId, payload) = decoded.Value;

        // Validate fields
        if (!MessageProtocol.ValidateMessageType(messageType))
        {
            Console.Error.WriteLine($"[MessageBusHost] Warning: Invalid message type '{messageType}', discarding.");
            return;
        }

        if (!MessageProtocol.ValidateCorrelationId(correlationId))
        {
            Console.Error.WriteLine($"[MessageBusHost] Warning: Invalid correlation ID '{correlationId}', discarding.");
            return;
        }

        if (!MessageProtocol.ValidatePayload(payload))
        {
            Console.Error.WriteLine("[MessageBusHost] Warning: Payload exceeds max length, discarding.");
            return;
        }

        // Find registered handler
        if (!_handlers.TryGetValue(messageType, out var handler))
        {
            Console.Error.WriteLine($"[MessageBusHost] Warning: No handler registered for message type '{messageType}', discarding.");
            return;
        }

        // Invoke handler
        string? responsePayload;
        try
        {
            responsePayload = await handler(correlationId, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MessageBusHost] Error: Handler for '{messageType}' threw: {ex.Message}");

            // Send system:error with the correlationId and error description
            var errorEnvelope = MessageProtocol.Encode("system:error", correlationId, ex.Message);
            _bridge.SendWebMessage(errorEnvelope);
            return;
        }

        // Null response = fire-and-forget, no wire message sent
        if (responsePayload is null)
            return;

        // Non-null (including empty string) = encode + send response
        var responseEnvelope = MessageProtocol.Encode(messageType, correlationId, responsePayload);
        _bridge.SendWebMessage(responseEnvelope);
    }
}
