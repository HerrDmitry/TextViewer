namespace TextViewer.Services;

/// <summary>
/// Paired byte + char lengths for a single line, used during unified scan batch append.
/// </summary>
internal readonly record struct LinePair(ulong ByteLength, ulong CharLength);
