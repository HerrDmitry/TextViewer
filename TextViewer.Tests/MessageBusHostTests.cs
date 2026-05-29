using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Unit tests for MessageBusHost.
/// Validates: Requirements 1.7, 1.8, 1.9, 10.6, 13.3
/// </summary>
public class MessageBusHostTests : IDisposable
{
    private readonly MockMessageBridge _bridge;
    private readonly MessageBusHost _host;

    public MessageBusHostTests()
    {
        _bridge = new MockMessageBridge();
        _host = new MessageBusHost(_bridge);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    /// <summary>
    /// Mock IMessageBridge for testing.
    /// </summary>
    private class MockMessageBridge : IMessageBridge
    {
        public List<string> SentMessages { get; } = new();
        public event EventHandler<string>? WebMessageReceived;

        public void SendWebMessage(string message) => SentMessages.Add(message);
        public void SimulateInbound(string message) => WebMessageReceived?.Invoke(this, message);
    }

    /// <summary>
    /// Test handler registration + invocation.
    /// Validates: Requirements 1.7, 1.8
    /// </summary>
    [Fact]
    public async Task RegisterHandler_WhenMessageReceived_InvokesHandler()
    {
        var handlerCalled = false;
        string? receivedCorrelationId = null;
        string? receivedPayload = null;

        _host.RegisterHandler("test-msg", async (correlationId, payload) =>
        {
            handlerCalled = true;
            receivedCorrelationId = correlationId;
            receivedPayload = payload;
            return "response-data";
        });

        var envelope = MessageProtocol.Encode("test-msg", "corr-123", "hello");
        _bridge.SimulateInbound(envelope);

        await Task.Delay(50);

        Assert.True(handlerCalled);
        Assert.Equal("corr-123", receivedCorrelationId);
        Assert.Equal("hello", receivedPayload);
    }

    /// <summary>
    /// Test response carries same Correlation_ID.
    /// Validates: Requirement 1.8
    /// </summary>
    [Fact]
    public async Task Handler_Response_CarriesSameCorrelationId()
    {
        _host.RegisterHandler("echo", async (correlationId, payload) =>
        {
            return "echoed";
        });

        var envelope = MessageProtocol.Encode("echo", "my-corr-id", "input");
        _bridge.SimulateInbound(envelope);

        await Task.Delay(50);

        Assert.Single(_bridge.SentMessages);
        var decoded = MessageProtocol.Decode(_bridge.SentMessages[0]);
        Assert.NotNull(decoded);
        Assert.Equal("echo", decoded.Value.MessageType);
        Assert.Equal("my-corr-id", decoded.Value.CorrelationId);
        Assert.Equal("echoed", decoded.Value.Payload);
    }

    /// <summary>
    /// Test Backend_Push sends with generated ID.
    /// Validates: Requirement 1.9
    /// </summary>
    [Fact]
    public void Send_BackendPush_SendsWithGeneratedCorrelationId()
    {
        _host.Send("notify-client", "some-data");

        Assert.Single(_bridge.SentMessages);
        var decoded = MessageProtocol.Decode(_bridge.SentMessages[0]);
        Assert.NotNull(decoded);
        Assert.Equal("notify-client", decoded.Value.MessageType);
        Assert.Equal("some-data", decoded.Value.Payload);
        // Correlation_ID should be a valid GUID format
        Assert.True(Guid.TryParse(decoded.Value.CorrelationId, out _));
    }

    /// <summary>
    /// Test handler exception → system:error sent.
    /// Validates: Requirement 13.3
    /// </summary>
    [Fact]
    public async Task Handler_ThrowsException_SendsSystemError()
    {
        _host.RegisterHandler("failing-op", async (correlationId, payload) =>
        {
            throw new InvalidOperationException("Something went wrong");
        });

        var envelope = MessageProtocol.Encode("failing-op", "err-corr-1", "trigger");
        _bridge.SimulateInbound(envelope);

        await Task.Delay(50);

        Assert.Single(_bridge.SentMessages);
        var decoded = MessageProtocol.Decode(_bridge.SentMessages[0]);
        Assert.NotNull(decoded);
        Assert.Equal("system:error", decoded.Value.MessageType);
        Assert.Equal("err-corr-1", decoded.Value.CorrelationId);
        Assert.Contains("Something went wrong", decoded.Value.Payload);
    }

    /// <summary>
    /// Test SendSystemMessage validates prefix.
    /// Validates: Requirement 10.6
    /// </summary>
    [Fact]
    public void SendSystemMessage_WithoutSystemPrefix_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _host.SendSystemMessage("not-system", "payload"));

        Assert.Contains("system:", ex.Message);
    }

    /// <summary>
    /// Test SendSystemMessage with valid prefix sends correctly.
    /// Validates: Requirement 10.6
    /// </summary>
    [Fact]
    public void SendSystemMessage_WithValidPrefix_Sends()
    {
        _host.SendSystemMessage("system:alert", "warning-data");

        Assert.Single(_bridge.SentMessages);
        var decoded = MessageProtocol.Decode(_bridge.SentMessages[0]);
        Assert.NotNull(decoded);
        Assert.Equal("system:alert", decoded.Value.MessageType);
        Assert.Equal("warning-data", decoded.Value.Payload);
        Assert.True(Guid.TryParse(decoded.Value.CorrelationId, out _));
    }

    /// <summary>
    /// Test unknown message type → no handler → discard + log.
    /// Validates: Requirement 1.8
    /// </summary>
    [Fact]
    public async Task UnknownMessageType_NoHandler_DiscardsMessage()
    {
        // Register a handler for a different type
        _host.RegisterHandler("known-type", async (correlationId, payload) => "ok");

        // Send a message with an unregistered type
        var envelope = MessageProtocol.Encode("unknown-type", "corr-456", "data");

        var stdErr = new StringWriter();
        Console.SetError(stdErr);

        _bridge.SimulateInbound(envelope);

        await Task.Delay(50);

        // No response should be sent
        Assert.Empty(_bridge.SentMessages);
        // Should log a warning
        Assert.Contains("unknown-type", stdErr.ToString());

        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    /// <summary>
    /// Test null handler response → no wire message sent.
    /// Validates: Design doc null-response behavior
    /// </summary>
    [Fact]
    public async Task Handler_ReturnsNull_NoWireMessageSent()
    {
        _host.RegisterHandler("fire-forget", async (correlationId, payload) =>
        {
            return null; // fire-and-forget
        });

        var envelope = MessageProtocol.Encode("fire-forget", "corr-789", "command");
        _bridge.SimulateInbound(envelope);

        await Task.Delay(50);

        // No message should be sent back
        Assert.Empty(_bridge.SentMessages);
    }

    /// <summary>
    /// Test empty-string handler response → wire message sent.
    /// Validates: Design doc empty-string response behavior
    /// </summary>
    [Fact]
    public async Task Handler_ReturnsEmptyString_WireMessageSent()
    {
        _host.RegisterHandler("ack-op", async (correlationId, payload) =>
        {
            return ""; // explicit acknowledgment with empty payload
        });

        var envelope = MessageProtocol.Encode("ack-op", "corr-abc", "request");
        _bridge.SimulateInbound(envelope);

        await Task.Delay(50);

        Assert.Single(_bridge.SentMessages);
        var decoded = MessageProtocol.Decode(_bridge.SentMessages[0]);
        Assert.NotNull(decoded);
        Assert.Equal("ack-op", decoded.Value.MessageType);
        Assert.Equal("corr-abc", decoded.Value.CorrelationId);
        Assert.Equal("", decoded.Value.Payload);
    }
}
