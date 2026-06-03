using System.Buffers.Binary;

namespace TextViewer.Services;

/// <summary>
/// A contiguous block of (Byte_Length, Char_Length) pairs stored in a single integer tier.
/// Data layout: [byteLen0, charLen0, byteLen1, charLen1, ...]
/// Both values in a pair use the same tier width.
/// </summary>
internal sealed class Segment
{
    private readonly byte[] _data;

    public int StartLine { get; }
    public int Count { get; }
    public IntegerTier Tier { get; }

    /// <summary>
    /// Exposes the raw data buffer for Interlocked operations by LineIndex.
    /// </summary>
    internal byte[] Data => _data;

    public Segment(int startLine, int count, IntegerTier tier, byte[] data)
    {
        StartLine = startLine;
        Count = count;
        Tier = tier;
        _data = data;
    }

    /// <summary>Gets the byte length (first value in pair) at the given offset within segment.</summary>
    public ulong GetByteLength(int offsetWithinSegment)
    {
        int tierSize = (int)Tier;
        int byteOffset = offsetWithinSegment * 2 * tierSize;
        return ReadValue(byteOffset, tierSize);
    }

    /// <summary>Gets the char length (second value in pair) at the given offset within segment.</summary>
    public ulong GetCharLength(int offsetWithinSegment)
    {
        int tierSize = (int)Tier;
        int charOffset = (offsetWithinSegment * 2 + 1) * tierSize;
        return ReadValue(charOffset, tierSize);
    }


    private ulong ReadValue(int offset, int tierSize)
    {
        var span = _data.AsSpan(offset, tierSize);
        return tierSize switch
        {
            1 => span[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(span),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(span),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(span),
            _ => throw new InvalidOperationException($"Unsupported tier size: {tierSize}")
        };
    }
}
