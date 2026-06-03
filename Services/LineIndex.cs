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
    private ulong[] _segmentPrefixBytes = [];
    private volatile int _lineCount;
    private long _totalByteLength;
    private long _maxByteLength;
    private long _maxCharLength;

    /// <summary>Total lines indexed (visible once scan appends).</summary>
    public int LineCount => _lineCount;

    /// <summary>Maximum byte length across all indexed lines. O(1).</summary>
    public ulong MaxByteLength => (ulong)Interlocked.Read(ref _maxByteLength);

    /// <summary>Maximum char length across all indexed lines. O(1).</summary>
    public ulong MaxCharLength => (ulong)Interlocked.Read(ref _maxCharLength);

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
    /// Returns char length for a given line (0-based). O(log N) lookup.
    /// Both byte and char lengths are written atomically per batch,
    /// so any visible line always has both values available.
    /// </summary>
    public ulong GetCharLength(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lineCount)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

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

        if (lineIndex == 0)
            return 0;

        if (lineIndex == _lineCount)
            return (ulong)Interlocked.Read(ref _totalByteLength);

        var (segment, segmentIndex) = _segments.FindSegmentWithIndex(lineIndex);
        ulong offset = _segmentPrefixBytes[segmentIndex];
        int offsetInSegment = lineIndex - segment.StartLine;

        for (int i = 0; i < offsetInSegment; i++)
        {
            offset += segment.GetByteLength(i);
        }

        return offset;
    }

    // --- Writer methods (internal, called by FileIndex during scan) ---

    /// <summary>
    /// Appends complete line pairs (byteLength, charLength) during unified scan.
    /// Thread-safety: holds _writeLock, writes segment data, then increments _lineCount.
    /// </summary>
    internal void AppendLinePairs(ReadOnlySpan<LinePair> pairs)
    {
        if (pairs.IsEmpty)
            return;

        lock (_writeLock)
        {
            int startLine = _lineCount;
            int oldSegmentCount = _segments.Segments.Count;
            ulong baseOffsetBeforeAppend = (ulong)_totalByteLength;
            _segments.Append(pairs, startLine);

            var segments = _segments.Segments;
            if (segments.Count > oldSegmentCount)
            {
                var updatedPrefixes = new ulong[segments.Count];
                if (_segmentPrefixBytes.Length > 0)
                    Array.Copy(_segmentPrefixBytes, updatedPrefixes, _segmentPrefixBytes.Length);

                ulong runningOffset = baseOffsetBeforeAppend;
                for (int i = oldSegmentCount; i < segments.Count; i++)
                {
                    updatedPrefixes[i] = runningOffset;

                    var segment = segments[i];
                    for (int j = 0; j < segment.Count; j++)
                    {
                        runningOffset += segment.GetByteLength(j);
                    }
                }

                _segmentPrefixBytes = updatedPrefixes;
            }

            // Track running maximums and total byte length.
            foreach (var pair in pairs)
            {
                if ((long)pair.ByteLength > _maxByteLength)
                    _maxByteLength = (long)pair.ByteLength;

                if ((long)pair.CharLength > _maxCharLength)
                    _maxCharLength = (long)pair.CharLength;

                _totalByteLength += (long)pair.ByteLength;
            }

            // Publish: increment _lineCount AFTER segment data is fully written.
            // volatile write ensures visibility ordering.
            _lineCount = startLine + pairs.Length;
        }
    }

    /// <summary>
    /// Resets the index to empty state. Called on scan abort or disposal.
    /// </summary>
    internal void Clear()
    {
        lock (_writeLock)
        {
            _lineCount = 0;
            _totalByteLength = 0;
            _maxByteLength = 0;
            _maxCharLength = 0;
            _segmentPrefixBytes = [];
            _segments.Clear();
        }
    }
}
