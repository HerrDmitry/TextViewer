using System.Threading.Channels;

namespace TextViewer.Services;

/// <summary>Outcome of a successfully dispatched message.</summary>
public enum DispatchOutcome
{
    /// <summary>Handler returned a response that was sent back.</summary>
    ResponseSent,
    /// <summary>Handler returned null — fire-and-forget, no wire message.</summary>
    FireAndForget
}

/// <summary>Reason message dispatch failed.</summary>
public enum DispatchErrorCode
{
    ParseFailure,
    InvalidMessageType,
    InvalidCorrelationId,
    PayloadTooLarge,
    NoHandler,
    HandlerException
}

/// <summary>Structured dispatch failure info.</summary>
public sealed record DispatchError(DispatchErrorCode Code, string Message);

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
        var result = await DispatchMessageAsync(rawMessage);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine($"[MessageBusHost] Warning: {result.Error.Message}");
        }
    }

    internal async Task<Result<DispatchOutcome, DispatchError>> DispatchMessageAsync(string rawMessage)
    {
        var decodeResult = MessageProtocol.Decode(rawMessage);
        if (!decodeResult.IsSuccess)
        {
            return Result<DispatchOutcome, DispatchError>.Failure(
                new DispatchError(DispatchErrorCode.ParseFailure, "Failed to parse message envelope, discarding."));
        }

        var (messageType, correlationId, payload) = decodeResult.Value;

        // Validate fields
        if (!MessageProtocol.ValidateMessageType(messageType))
        {
            return Result<DispatchOutcome, DispatchError>.Failure(
                new DispatchError(DispatchErrorCode.InvalidMessageType, $"Invalid message type '{messageType}', discarding."));
        }

        if (!MessageProtocol.ValidateCorrelationId(correlationId))
        {
            return Result<DispatchOutcome, DispatchError>.Failure(
                new DispatchError(DispatchErrorCode.InvalidCorrelationId, $"Invalid correlation ID '{correlationId}', discarding."));
        }

        if (!MessageProtocol.ValidatePayload(payload))
        {
            return Result<DispatchOutcome, DispatchError>.Failure(
                new DispatchError(DispatchErrorCode.PayloadTooLarge, "Payload exceeds max length, discarding."));
        }

        // Find registered handler
        if (!_handlers.TryGetValue(messageType, out var handler))
        {
            return Result<DispatchOutcome, DispatchError>.Failure(
                new DispatchError(DispatchErrorCode.NoHandler, $"No handler registered for message type '{messageType}', discarding."));
        }

        // Invoke handler
        string? responsePayload;
        try
        {
            responsePayload = await handler(correlationId, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Send system:error with the correlationId and error description
            var errorEnvelope = MessageProtocol.Encode("system:error", correlationId, ex.Message);
            _bridge.SendWebMessage(errorEnvelope);
            return Result<DispatchOutcome, DispatchError>.Failure(
                new DispatchError(DispatchErrorCode.HandlerException, $"Handler for '{messageType}' threw: {ex.Message}"));
        }

        // Null response = fire-and-forget, no wire message sent
        if (responsePayload is null)
            return Result<DispatchOutcome, DispatchError>.Success(DispatchOutcome.FireAndForget);

        // Non-null (including empty string) = encode + send response
        var responseEnvelope = MessageProtocol.Encode(messageType, correlationId, responsePayload);
        _bridge.SendWebMessage(responseEnvelope);
        return Result<DispatchOutcome, DispatchError>.Success(DispatchOutcome.ResponseSent);
    }
}
