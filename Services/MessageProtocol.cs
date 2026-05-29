using System.Text.RegularExpressions;

namespace TextViewer.Services;

/// <summary>
/// Shared message protocol for encoding/decoding wire envelopes.
/// Wire format: Message_Type\nCorrelation_ID\npayload
/// </summary>
public static partial class MessageProtocol
{
    private const int MaxMessageTypeLength = 64;
    private const int MaxCorrelationIdLength = 36;
    private const int MaxPayloadLength = 2_097_152;

    [GeneratedRegex(@"^[a-z0-9:\-]+$")]
    private static partial Regex MessageTypeRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9\-]+$")]
    private static partial Regex CorrelationIdRegex();

    /// <summary>
    /// Encodes a message into wire envelope format.
    /// No-payload and empty-string payload produce identical output.
    /// </summary>
    public static string Encode(string messageType, string correlationId, string? payload)
    {
        return $"{messageType}\n{correlationId}\n{payload ?? string.Empty}";
    }

    /// <summary>
    /// Decodes a raw wire envelope into its constituent parts.
    /// Returns null if the raw string does not contain at least two newline characters.
    /// Splits on first two \n occurrences only — payload may contain additional newlines.
    /// </summary>
    public static (string MessageType, string CorrelationId, string Payload)? Decode(string raw)
    {
        if (raw is null)
            return null;

        var firstNewline = raw.IndexOf('\n');
        if (firstNewline < 0)
            return null;

        var secondNewline = raw.IndexOf('\n', firstNewline + 1);
        if (secondNewline < 0)
            return null;

        var messageType = raw[..firstNewline];
        var correlationId = raw[(firstNewline + 1)..secondNewline];
        var payload = raw[(secondNewline + 1)..];

        return (messageType, correlationId, payload);
    }

    /// <summary>
    /// Validates a Message_Type: regex [a-z0-9:-]+, 1–64 chars.
    /// </summary>
    public static bool ValidateMessageType(string type)
    {
        if (string.IsNullOrEmpty(type))
            return false;

        if (type.Length > MaxMessageTypeLength)
            return false;

        return MessageTypeRegex().IsMatch(type);
    }

    /// <summary>
    /// Validates a Correlation_ID: regex [a-zA-Z0-9-]+, 1–36 chars.
    /// </summary>
    public static bool ValidateCorrelationId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        if (id.Length > MaxCorrelationIdLength)
            return false;

        return CorrelationIdRegex().IsMatch(id);
    }

    /// <summary>
    /// Validates a payload: length ≤ 2,097,152 chars. Null treated as empty (valid).
    /// </summary>
    public static bool ValidatePayload(string? payload)
    {
        if (payload is null)
            return true;

        return payload.Length <= MaxPayloadLength;
    }
}
