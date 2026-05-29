namespace TextViewer.Services;

/// <summary>
/// Sorted collection of segments enabling O(log N) line-to-segment lookup.
/// Single directory storing interleaved (Byte_Length, Char_Length) pairs.
/// </summary>
internal sealed class SegmentDirectory
{
    private readonly List<Segment> _segments = new();

    /// <summary>Exposes segments for testing purposes.</summary>
    internal IReadOnlyList<Segment> Segments => _segments;

    /// <summary>Total number of lines stored across all segments.</summary>
    public int TotalLines { get; private set; }

    /// <summary>Finds the segment containing the given line index via binary search.</summary>
    public Segment FindSegment(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= TotalLines)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        int lo = 0;
        int hi = _segments.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var seg = _segments[mid];

            if (lineIndex < seg.StartLine)
            {
                hi = mid - 1;
            }
            else if (lineIndex >= seg.StartLine + seg.Count)
            {
                lo = mid + 1;
            }
            else
            {
                return seg;
            }
        }

        throw new InvalidOperationException($"No segment found for line index {lineIndex}");
    }

    /// <summary>
    /// Appends pairs, creating/extending segments with optimal tier selection.
    /// Tier determined by max byte length value (first element of each pair).
    /// Each line is stored as a pair (byteLength, 0) — char length filled later.
    /// </summary>
    public void Append(ReadOnlySpan<ulong> byteLengths, int startLineIndex)
    {
        if (byteLengths.IsEmpty)
            return;

        // Phase 1: Build initial segments using greedy tier-based grouping
        int i = 0;
        while (i < byteLengths.Length)
        {
            var tier = SelectTier(byteLengths[i]);

            // Check if we can extend the last segment
            if (_segments.Count > 0)
            {
                var lastSeg = _segments[^1];
                var lastTier = lastSeg.Tier;

                if (tier == lastTier)
                {
                    // Same tier — extend the current segment
                    int runLength = CountRunAtTier(byteLengths, i, lastTier);
                    ExtendSegment(lastSeg, byteLengths.Slice(i, runLength));
                    i += runLength;
                    continue;
                }
                else if (tier > lastTier)
                {
                    // Widening — start a new segment
                    // Fall through to create new segment below
                }
                else
                {
                    // Narrowing — start a new narrower segment
                    // Fall through to create new segment below
                }
            }

            // Create a new segment for the current tier run
            int segRunLength = CountRunFittingTier(byteLengths, i, tier);
            ulong maxInRun = 0;
            for (int j = i; j < i + segRunLength; j++)
            {
                if (byteLengths[j] > maxInRun)
                    maxInRun = byteLengths[j];
            }
            tier = SelectTier(maxInRun);

            CreateSegment(startLineIndex + i, byteLengths.Slice(i, segRunLength), tier);
            i += segRunLength;
        }

        // Phase 2: Optimize segment boundaries
        OptimizeSegments();

        TotalLines = startLineIndex + byteLengths.Length;
    }

    /// <summary>
    /// Optimizes segment boundaries by merging adjacent segments when profitable
    /// and splitting segments when profitable. Iterates until no more improvements found.
    /// </summary>
    private void OptimizeSegments()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;

            // Pass 1: Merge adjacent segments when merging reduces memory
            for (int s = 0; s < _segments.Count - 1; s++)
            {
                var seg1 = _segments[s];
                var seg2 = _segments[s + 1];

                long currentMemory = SegmentMemory(seg1.Count, seg1.Tier)
                                   + SegmentMemory(seg2.Count, seg2.Tier);

                // Determine merged tier
                ulong max1 = GetMaxByteLength(seg1);
                ulong max2 = GetMaxByteLength(seg2);
                var mergedTier = SelectTier(Math.Max(max1, max2));

                long mergedMemory = SegmentMemory(seg1.Count + seg2.Count, mergedTier);

                if (mergedMemory < currentMemory)
                {
                    // Merge is profitable — combine the two segments
                    var mergedData = MergeSegmentData(seg1, seg2, mergedTier);
                    var merged = new Segment(seg1.StartLine, seg1.Count + seg2.Count, mergedTier, mergedData);
                    _segments[s] = merged;
                    _segments.RemoveAt(s + 1);
                    changed = true;
                    s--; // Re-check this position
                }
            }

            // Pass 2: Split segments when splitting reduces memory
            for (int s = 0; s < _segments.Count; s++)
            {
                var segment = _segments[s];
                if (segment.Count < 2)
                    continue;

                // Find the best split point
                int bestSplit = -1;
                long bestSavings = 0;

                for (int splitAt = 1; splitAt < segment.Count; splitAt++)
                {
                    ulong maxLeft = 0;
                    for (int k = 0; k < splitAt; k++)
                    {
                        var val = segment.GetByteLength(k);
                        if (val > maxLeft) maxLeft = val;
                    }

                    ulong maxRight = 0;
                    for (int k = splitAt; k < segment.Count; k++)
                    {
                        var val = segment.GetByteLength(k);
                        if (val > maxRight) maxRight = val;
                    }

                    var leftTier = SelectTier(maxLeft);
                    var rightTier = SelectTier(maxRight);

                    long currentMemory = SegmentMemory(segment.Count, segment.Tier);
                    long splitMemory = SegmentMemory(splitAt, leftTier)
                                     + SegmentMemory(segment.Count - splitAt, rightTier);

                    long savings = currentMemory - splitMemory;
                    if (savings > bestSavings)
                    {
                        bestSavings = savings;
                        bestSplit = splitAt;
                    }
                }

                if (bestSplit > 0)
                {
                    // Split is profitable
                    var (left, right) = SplitSegment(segment, bestSplit);
                    _segments[s] = left;
                    _segments.Insert(s + 1, right);
                    changed = true;
                }
            }
        }
    }

    /// <summary>Gets the maximum byte length value in a segment.</summary>
    private static ulong GetMaxByteLength(Segment segment)
    {
        ulong max = 0;
        for (int i = 0; i < segment.Count; i++)
        {
            var val = segment.GetByteLength(i);
            if (val > max) max = val;
        }
        return max;
    }

    /// <summary>Merges two segments' data into a single byte array at the given tier.</summary>
    private static byte[] MergeSegmentData(Segment seg1, Segment seg2, IntegerTier tier)
    {
        int tierSize = (int)tier;
        int totalCount = seg1.Count + seg2.Count;
        byte[] data = new byte[totalCount * 2 * tierSize];

        for (int i = 0; i < seg1.Count; i++)
        {
            int offset = i * 2 * tierSize;
            WriteValue(data, offset, tierSize, seg1.GetByteLength(i));
            WriteValue(data, offset + tierSize, tierSize, seg1.GetCharLength(i));
        }

        for (int i = 0; i < seg2.Count; i++)
        {
            int offset = (seg1.Count + i) * 2 * tierSize;
            WriteValue(data, offset, tierSize, seg2.GetByteLength(i));
            WriteValue(data, offset + tierSize, tierSize, seg2.GetCharLength(i));
        }

        return data;
    }

    /// <summary>Splits a segment at the given position into two segments.</summary>
    private static (Segment left, Segment right) SplitSegment(Segment segment, int splitAt)
    {
        // Determine tiers for each half
        ulong maxLeft = 0;
        for (int i = 0; i < splitAt; i++)
        {
            var val = segment.GetByteLength(i);
            if (val > maxLeft) maxLeft = val;
        }

        ulong maxRight = 0;
        for (int i = splitAt; i < segment.Count; i++)
        {
            var val = segment.GetByteLength(i);
            if (val > maxRight) maxRight = val;
        }

        var leftTier = SelectTier(maxLeft);
        var rightTier = SelectTier(maxRight);

        int leftTierSize = (int)leftTier;
        int rightTierSize = (int)rightTier;

        // Build left segment data
        byte[] leftData = new byte[splitAt * 2 * leftTierSize];
        for (int i = 0; i < splitAt; i++)
        {
            int offset = i * 2 * leftTierSize;
            WriteValue(leftData, offset, leftTierSize, segment.GetByteLength(i));
            WriteValue(leftData, offset + leftTierSize, leftTierSize, segment.GetCharLength(i));
        }

        // Build right segment data
        int rightCount = segment.Count - splitAt;
        byte[] rightData = new byte[rightCount * 2 * rightTierSize];
        for (int i = 0; i < rightCount; i++)
        {
            int offset = i * 2 * rightTierSize;
            WriteValue(rightData, offset, rightTierSize, segment.GetByteLength(splitAt + i));
            WriteValue(rightData, offset + rightTierSize, rightTierSize, segment.GetCharLength(splitAt + i));
        }

        var left = new Segment(segment.StartLine, splitAt, leftTier, leftData);
        var right = new Segment(segment.StartLine + splitAt, rightCount, rightTier, rightData);

        return (left, right);
    }

    /// <summary>Computes memory cost of a segment.</summary>
    private static long SegmentMemory(int count, IntegerTier tier)
    {
        return 9 + (long)count * 2 * (int)tier;
    }

    /// <summary>Clears all segments and resets line count to zero.</summary>
    public void Clear()
    {
        _segments.Clear();
        TotalLines = 0;
    }

    /// <summary>
    /// Updates the char-length slot of an existing pair in-place.
    /// Writes to the second value in the pair at the given line index.
    /// </summary>
    public void SetCharLength(int lineIndex, ulong charLength)
    {
        var segment = FindSegment(lineIndex);
        int offset = lineIndex - segment.StartLine;
        segment.SetCharLength(offset, charLength);
    }

    /// <summary>Selects the smallest tier that can hold the given value.</summary>
    internal static IntegerTier SelectTier(ulong maxByteLength)
    {
        if (maxByteLength <= 255) return IntegerTier.Byte;
        if (maxByteLength <= 65535) return IntegerTier.UShort;
        if (maxByteLength <= 4294967295) return IntegerTier.UInt;
        return IntegerTier.ULong;
    }

    /// <summary>Counts consecutive lines starting at index that fit within the given tier.</summary>
    private static int CountRunAtTier(ReadOnlySpan<ulong> byteLengths, int startIndex, IntegerTier tier)
    {
        int count = 0;
        for (int j = startIndex; j < byteLengths.Length; j++)
        {
            if (SelectTier(byteLengths[j]) != tier)
                break;
            count++;
        }
        return count;
    }

    /// <summary>Counts consecutive lines starting at index that fit within the given tier (value fits, not exact match).</summary>
    private static int CountRunFittingTier(ReadOnlySpan<ulong> byteLengths, int startIndex, IntegerTier tier)
    {
        ulong maxValue = tier switch
        {
            IntegerTier.Byte => 255,
            IntegerTier.UShort => 65535,
            IntegerTier.UInt => 4294967295,
            IntegerTier.ULong => ulong.MaxValue,
            _ => throw new InvalidOperationException($"Unsupported tier: {tier}")
        };

        int count = 0;
        for (int j = startIndex; j < byteLengths.Length; j++)
        {
            if (byteLengths[j] > maxValue)
                break;
            count++;
        }
        return count == 0 ? 1 : count;
    }

    private void CreateSegment(int startLine, ReadOnlySpan<ulong> byteLengths, IntegerTier tier)
    {
        int tierSize = (int)tier;
        byte[] data = new byte[byteLengths.Length * 2 * tierSize];

        for (int i = 0; i < byteLengths.Length; i++)
        {
            int byteOffset = i * 2 * tierSize;
            WriteValue(data, byteOffset, tierSize, byteLengths[i]);
            // Char length slot initialized to 0 (already zero in new array)
        }

        var segment = new Segment(startLine, byteLengths.Length, tier, data);
        _segments.Add(segment);
    }

    private void ExtendSegment(Segment existing, ReadOnlySpan<ulong> byteLengths)
    {
        int tierSize = (int)existing.Tier;
        int existingDataLength = existing.Count * 2 * tierSize;
        int newDataLength = (existing.Count + byteLengths.Length) * 2 * tierSize;

        byte[] newData = new byte[newDataLength];
        Array.Copy(existing.Data, newData, existingDataLength);

        for (int i = 0; i < byteLengths.Length; i++)
        {
            int byteOffset = existingDataLength + i * 2 * tierSize;
            WriteValue(newData, byteOffset, tierSize, byteLengths[i]);
            // Char length slot initialized to 0 (already zero in new array)
        }

        var newSegment = new Segment(existing.StartLine, existing.Count + byteLengths.Length, existing.Tier, newData);

        // Replace the last segment
        _segments[^1] = newSegment;
    }

    private static void WriteValue(byte[] data, int offset, int tierSize, ulong value)
    {
        var span = data.AsSpan(offset, tierSize);
        switch (tierSize)
        {
            case 1:
                span[0] = (byte)value;
                break;
            case 2:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)value);
                break;
            case 4:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)value);
                break;
            case 8:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(span, value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported tier size: {tierSize}");
        }
    }
}
