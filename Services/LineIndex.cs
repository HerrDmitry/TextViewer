using System.Runtime.CompilerServices;
using System.Threading;

namespace TextViewer.Services;

/// <summary>
/// Thread-safe, memory-compact index of per-line lengths.
/// Single writer appends during scan; multiple readers query concurrently.
/// Uses a single SegmentDirectory storing (Byte_Length, Char_Length) pairs per line.
/// </summary>
public sealed class LineIndex
{
    private readonly object _writeLock = new();
    private SegmentDirectory _segments = new();
    private volatile int _lineCount;
    private volatile int _charLengthsWrittenUpTo;

    /// <summary>Total lines indexed (visible once Quick_Scan appends).</summary>
    public int LineCount => _lineCount;

    /// <summary>Returns byte length for a given line (0-based). O(log N) lookup.</summary>
    public ulong GetByteLength(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lineCount)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        var segment = _segments.FindSegment(lineIndex);
        int offset = lineIndex - segment.StartLine;
        return segment.GetByteLength(offset);
    }

    /// <summary>
    /// Returns char length for a given line (0-based), or null if Full_Scan
    /// has not yet reached this line. Uses volatile _charLengthsWrittenUpTo
    /// counter: returns null when lineIndex >= _charLengthsWrittenUpTo,
    /// otherwise reads from segment. O(log N) lookup.
    /// </summary>
    public ulong? GetCharLength(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lineCount)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        if (lineIndex >= _charLengthsWrittenUpTo)
            return null;

        var segment = _segments.FindSegment(lineIndex);
        int offset = lineIndex - segment.StartLine;
        return segment.GetCharLength(offset);
    }

    /// <summary>
    /// Returns the byte offset of the given line from the start of the file.
    /// Computed as the sum of Byte_Lengths for lines 0 through lineIndex-1.
    /// GetByteOffset(0) == 0. GetByteOffset(LineCount) == file size.
    /// </summary>
    public ulong GetByteOffset(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex > _lineCount)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        ulong offset = 0;
        for (int i = 0; i < lineIndex; i++)
        {
            var segment = _segments.FindSegment(i);
            int offsetInSegment = i - segment.StartLine;
            offset += segment.GetByteLength(offsetInSegment);
        }
        return offset;
    }

    // --- Writer methods (internal, called by FileIndex during scan) ---

    /// <summary>
    /// Appends line pairs during Quick_Scan. Each pair is (byteLength, 0).
    /// Char_Length slot initialized to 0, written later by Full_Scan.
    /// Thread-safety: holds _writeLock, writes segment data, then increments _lineCount.
    /// </summary>
    internal void AppendByteLengths(ReadOnlySpan<ulong> byteLengths)
    {
        if (byteLengths.IsEmpty)
            return;

        lock (_writeLock)
        {
            int startLine = _lineCount;
            _segments.Append(byteLengths, startLine);
            // Publish: increment _lineCount AFTER segment data is fully written.
            // volatile write ensures visibility ordering.
            _lineCount = startLine + byteLengths.Length;
        }
    }

    /// <summary>
    /// Writes the char length into the second slot of an existing pair.
    /// Called by Full_Scan for each line after Quick_Scan has populated the pair.
    /// Uses Interlocked operations for atomic writes, then increments _charLengthsWrittenUpTo.
    /// </summary>
    internal void SetCharLength(int lineIndex, ulong charLength)
    {
        var segment = _segments.FindSegment(lineIndex);
        int offsetInSegment = lineIndex - segment.StartLine;
        int tierSize = (int)segment.Tier;
        int charByteOffset = (offsetInSegment * 2 + 1) * tierSize;

        // Atomic write to the char-length slot using Interlocked operations.
        // For Byte tier (1 byte), a single byte write is atomic on all platforms.
        // For wider tiers, use Interlocked.Exchange via Unsafe.As.
        byte[] data = segment.Data;
        switch (tierSize)
        {
            case 1:
                // Single byte write is inherently atomic
                data[charByteOffset] = (byte)charLength;
                break;
            case 2:
                ref short slot16 = ref Unsafe.As<byte, short>(
                    ref data[charByteOffset]);
                Interlocked.Exchange(ref slot16, (short)(ushort)charLength);
                break;
            case 4:
                ref int slot32 = ref Unsafe.As<byte, int>(
                    ref data[charByteOffset]);
                Interlocked.Exchange(ref slot32, (int)(uint)charLength);
                break;
            case 8:
                ref long slot64 = ref Unsafe.As<byte, long>(
                    ref data[charByteOffset]);
                Interlocked.Exchange(ref slot64, (long)charLength);
                break;
        }

        // Publish: increment _charLengthsWrittenUpTo AFTER the Interlocked write completes.
        // volatile write ensures readers see the char-length value before seeing the counter update.
        _charLengthsWrittenUpTo = lineIndex + 1;
    }

    /// <summary>
    /// Marks all char lengths as written. Called after Full_Scan completes.
    /// Sets _charLengthsWrittenUpTo = _lineCount so all lines report char lengths.
    /// </summary>
    internal void FinalizeCharLengths()
    {
        _charLengthsWrittenUpTo = _lineCount;
    }

    /// <summary>
    /// Resets the index to empty state. Called on Quick_Scan abort or disposal.
    /// </summary>
    internal void Clear()
    {
        lock (_writeLock)
        {
            _lineCount = 0;
            _charLengthsWrittenUpTo = 0;
            _segments.Clear();
        }
    }
}
