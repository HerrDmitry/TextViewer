using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Property-based tests for MessageBusHost sequential handler processing.
/// Validates: Requirements 14.1, 14.2, 14.4
/// </summary>
public class MessageBusHostPropertyTests
{
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
    /// Property 18: Sequential host handler processing
    /// For any sequence of messages received by Message_Bus_Host, handlers SHALL be invoked
    /// sequentially — never concurrently — in arrival order.
    ///
    /// Validates: Requirements 14.1, 14.2, 14.4
    /// </summary>
    [Property(MaxTest = 100)]
    public Property SequentialHandlerProcessing_NoConcurrentExecution()
    {
        // Generate a random count of messages (1–20)
        var messageCountGen = Gen.Choose(1, 20);

        return Prop.ForAll(
            Arb.From(messageCountGen),
            messageCount =>
            {
                return SequentialProcessingHolds(messageCount).Result;
            });
    }

    private static async Task<Property> SequentialProcessingHolds(int messageCount)
    {
        var bridge = new MockMessageBridge();
        using var host = new MessageBusHost(bridge);

        var concurrencyCounter = 0;
        var maxConcurrency = 0;
        var invocationOrder = new List<int>();
        var lockObj = new object();

        // Register a handler that tracks concurrency via Interlocked operations
        host.RegisterHandler("test-type", async (correlationId, payload) =>
        {
            var current = Interlocked.Increment(ref concurrencyCounter);

            lock (lockObj)
            {
                if (current > maxConcurrency)
                    maxConcurrency = current;
                invocationOrder.Add(int.Parse(payload));
            }

            // Simulate async work with a small delay to create concurrency opportunity
            await Task.Delay(Random.Shared.Next(1, 10));

            Interlocked.Decrement(ref concurrencyCounter);

            return "ok";
        });

        // Send N messages rapidly to create concurrency pressure
        for (var i = 0; i < messageCount; i++)
        {
            var envelope = MessageProtocol.Encode("test-type", Guid.NewGuid().ToString(), i.ToString());
            bridge.SimulateInbound(envelope);
        }

        // Wait for all messages to be processed
        // The channel processes sequentially, so we wait enough time for all to complete
        await Task.Delay(messageCount * 15 + 200);

        var noConcurrency = maxConcurrency <= 1;
        var allProcessed = invocationOrder.Count == messageCount;
        var inOrder = invocationOrder.SequenceEqual(Enumerable.Range(0, messageCount));

        return (noConcurrency && allProcessed && inOrder)
            .Label($"maxConcurrency={maxConcurrency} (expected ≤1), " +
                   $"processed={invocationOrder.Count}/{messageCount}, " +
                   $"inOrder={inOrder}");
    }
}
