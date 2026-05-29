using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Property-based tests for MessageProtocol encode/decode round-trip.
/// Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.7
/// </summary>
public class MessageProtocolPropertyTests
{
    /// <summary>
    /// Custom Arbitrary for valid Message_Type: regex [a-z0-9:-]+, 1–64 chars.
    /// </summary>
    private static Arbitrary<string> ValidMessageType()
    {
        var chars = "abcdefghijklmnopqrstuvwxyz0123456789:-".ToCharArray();
        var gen = Gen.Choose(1, 64)
            .SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(chars), len)
                    .Select(arr => new string(arr)));
        return Arb.From(gen);
    }

    /// <summary>
    /// Custom Arbitrary for valid Correlation_ID: regex [a-zA-Z0-9-]+, 1–36 chars.
    /// </summary>
    private static Arbitrary<string> ValidCorrelationId()
    {
        var chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-".ToCharArray();
        var gen = Gen.Choose(1, 36)
            .SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(chars), len)
                    .Select(arr => new string(arr)));
        return Arb.From(gen);
    }

    /// <summary>
    /// Custom Arbitrary for payload: any string 0–1000 chars (including newlines, unicode).
    /// </summary>
    private static Arbitrary<string> ValidPayload()
    {
        var gen = Gen.Choose(0, 1000)
            .SelectMany(len =>
                Gen.ArrayOf(Gen.Choose(0, 65535).Select(i => (char)i), len)
                    .Select(arr => new string(arr)));
        return Arb.From(gen);
    }

    /// <summary>
    /// Property 1: Protocol round-trip (C#)
    /// For any valid Message_Type, Correlation_ID, and payload (including payloads containing
    /// newlines), encoding then decoding SHALL produce values identical to the original inputs.
    /// 
    /// Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.7
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ProtocolRoundTrip_DecodingEncodedMessage_ReturnsOriginalValues()
    {
        return Prop.ForAll(
            ValidMessageType(),
            ValidCorrelationId(),
            ValidPayload(),
            (messageType, correlationId, payload) =>
            {
                var encoded = MessageProtocol.Encode(messageType, correlationId, payload);
                var decoded = MessageProtocol.Decode(encoded);

                return (decoded != null &&
                        decoded.Value.MessageType == messageType &&
                        decoded.Value.CorrelationId == correlationId &&
                        decoded.Value.Payload == payload)
                    .Label($"Expected ({messageType}, {correlationId}, {payload?.Length ?? 0} chars) " +
                           $"but got {(decoded == null ? "null" : $"({decoded.Value.MessageType}, {decoded.Value.CorrelationId}, {decoded.Value.Payload.Length} chars)")}");
            });
    }
}
