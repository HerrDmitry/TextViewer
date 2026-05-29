using TextViewer.Services;

namespace TextViewer.Tests.Services;

public class LineIndexTests
{
    // --- Zero-line file → no segments, LineCount == 0 ---

    [Fact]
    public void ZeroLineFile_NoSegments_LineCountZero()
    {
        var index = new LineIndex();

        Assert.Equal(0, index.LineCount);
    }

    [Fact]
    public void ZeroLineFile_AppendEmpty_NoSegments()
    {
        var index = new LineIndex();
        index.AppendByteLengths(ReadOnlySpan<ulong>.Empty);

        Assert.Equal(0, index.LineCount);
    }

    // --- Single-line file → one segment with one pair ---

    [Fact]
    public void SingleLineFile_OneSegment_OnePair()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 42 });

        Assert.Equal(1, index.LineCount);
        Assert.Equal(42UL, index.GetByteLength(0));
    }

    // --- Tier widening at segment boundary ---

    [Fact]
    public void TierWidening_AtSegmentBoundary_CreatesNewSegment()
    {
        var dir = new SegmentDirectory();
        // Many lines fit in Byte tier, then one line requires UShort tier.
        // With enough Byte-tier lines, keeping them in a separate Byte segment
        // is cheaper than widening them all to UShort.
        // Separate: 9 + (5×2×1) + 9 + (1×2×2) = 19 + 13 = 32
        // Merged (UShort): 9 + (6×2×2) = 33
        // So separate is cheaper when there are enough Byte-tier lines.
        dir.Append(new ulong[] { 100, 100, 100, 100, 100, 300 }, 0);

        var segments = dir.Segments;
        Assert.Equal(2, segments.Count);
        Assert.Equal(IntegerTier.Byte, segments[0].Tier);
        Assert.Equal(IntegerTier.UShort, segments[1].Tier);
    }

    // --- Tier narrowing at segment boundary (savings > 9) ---

    [Fact]
    public void TierNarrowing_WhenSavingsExceedMetadataCost_SplitsSegment()
    {
        var dir = new SegmentDirectory();
        // First line requires UInt tier (value > 65535)
        // Followed by many lines that fit in Byte tier
        // Memory saved = remainingLines * 2 * (4 - 1) = remainingLines * 6
        // Need remainingLines * 6 > 9 → remainingLines >= 2
        var byteLengths = new ulong[12];
        byteLengths[0] = 70000; // UInt tier
        for (int i = 1; i < 12; i++)
            byteLengths[i] = 50; // Byte tier

        dir.Append(byteLengths, 0);

        var segments = dir.Segments;
        Assert.True(segments.Count >= 2, "Should split into at least 2 segments");
        Assert.Equal(IntegerTier.UInt, segments[0].Tier);
        Assert.Equal(IntegerTier.Byte, segments[1].Tier);
    }

    // --- Narrowing NOT applied when savings ≤ 9 ---

    [Fact]
    public void TierNarrowing_WhenSavingsDoNotExceedMetadataCost_DoesNotSplit()
    {
        var dir = new SegmentDirectory();
        // First line requires UShort tier (value > 255)
        // Followed by 1 line that fits in Byte tier
        // Memory saved = 1 * 2 * (2 - 1) = 2, which is ≤ 9
        // Should NOT split
        var byteLengths = new ulong[] { 300, 50 };

        dir.Append(byteLengths, 0);

        var segments = dir.Segments;
        Assert.Single(segments);
        Assert.Equal(IntegerTier.UShort, segments[0].Tier);
    }

    // --- Segment memory == 9 + Count × 2 × TierSize ---

    [Fact]
    public void SegmentMemory_ByteTier_MatchesFormula()
    {
        var dir = new SegmentDirectory();
        dir.Append(new ulong[] { 10, 20, 30 }, 0);

        var segment = dir.Segments[0];
        int expectedMetadata = 9; // StartLine(4) + Count(4) + Tier(1)
        int expectedDataSize = segment.Count * 2 * (int)segment.Tier;
        int expectedTotal = expectedMetadata + expectedDataSize;

        // Data.Length gives the data size (without metadata)
        Assert.Equal(expectedDataSize, segment.Data.Length);
        // Total memory = metadata + data
        Assert.Equal(expectedTotal, 9 + segment.Data.Length);
    }

    [Fact]
    public void SegmentMemory_UShortTier_MatchesFormula()
    {
        var dir = new SegmentDirectory();
        dir.Append(new ulong[] { 300, 400, 500 }, 0);

        var segment = dir.Segments[0];
        int expectedDataSize = segment.Count * 2 * (int)segment.Tier;

        Assert.Equal(IntegerTier.UShort, segment.Tier);
        Assert.Equal(expectedDataSize, segment.Data.Length);
    }

    [Fact]
    public void SegmentMemory_UIntTier_MatchesFormula()
    {
        var dir = new SegmentDirectory();
        dir.Append(new ulong[] { 70000, 80000 }, 0);

        var segment = dir.Segments[0];
        int expectedDataSize = segment.Count * 2 * (int)segment.Tier;

        Assert.Equal(IntegerTier.UInt, segment.Tier);
        Assert.Equal(expectedDataSize, segment.Data.Length);
    }

    // --- GetByteOffset(0) == 0 ---

    [Fact]
    public void GetByteOffset_Zero_ReturnsZero()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 10, 20, 30 });

        Assert.Equal(0UL, index.GetByteOffset(0));
    }

    // --- GetByteOffset(N) == sum of Byte_Lengths[0..N-1] ---

    [Fact]
    public void GetByteOffset_N_ReturnsSumOfPreviousByteLengths()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 10, 20, 30, 40 });

        Assert.Equal(0UL, index.GetByteOffset(0));
        Assert.Equal(10UL, index.GetByteOffset(1));
        Assert.Equal(30UL, index.GetByteOffset(2));  // 10 + 20
        Assert.Equal(60UL, index.GetByteOffset(3));  // 10 + 20 + 30
        Assert.Equal(100UL, index.GetByteOffset(4)); // 10 + 20 + 30 + 40 (== LineCount)
    }

    // --- GetCharLength returns null before Full_Scan writes ---

    [Fact]
    public void GetCharLength_BeforeFullScan_ReturnsNull()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 10, 20, 30 });

        Assert.Null(index.GetCharLength(0));
        Assert.Null(index.GetCharLength(1));
        Assert.Null(index.GetCharLength(2));
    }

    // --- GetCharLength returns value after SetCharLength ---

    [Fact]
    public void GetCharLength_AfterSetCharLength_ReturnsValue()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 10, 20, 30 });

        index.SetCharLength(0, 8);
        index.SetCharLength(1, 15);
        index.SetCharLength(2, 25);

        Assert.Equal(8UL, index.GetCharLength(0));
        Assert.Equal(15UL, index.GetCharLength(1));
        Assert.Equal(25UL, index.GetCharLength(2));
    }

    // --- SetCharLength writes to char slot without affecting byte slot ---

    [Fact]
    public void SetCharLength_DoesNotAffectByteSlot()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 100, 200 });

        // Record byte lengths before
        var byteLenBefore0 = index.GetByteLength(0);
        var byteLenBefore1 = index.GetByteLength(1);

        // Write char lengths
        index.SetCharLength(0, 50);
        index.SetCharLength(1, 150);

        // Byte lengths should be unchanged
        Assert.Equal(byteLenBefore0, index.GetByteLength(0));
        Assert.Equal(byteLenBefore1, index.GetByteLength(1));
        Assert.Equal(100UL, index.GetByteLength(0));
        Assert.Equal(200UL, index.GetByteLength(1));
    }

    // --- Segment stores interleaved pairs (byteLen, charLen) ---

    [Fact]
    public void Segment_StoresInterleavedPairs()
    {
        var dir = new SegmentDirectory();
        dir.Append(new ulong[] { 10, 20, 30 }, 0);

        var segment = dir.Segments[0];

        // Verify byte lengths stored correctly
        Assert.Equal(10UL, segment.GetByteLength(0));
        Assert.Equal(20UL, segment.GetByteLength(1));
        Assert.Equal(30UL, segment.GetByteLength(2));

        // Char lengths initialized to 0
        Assert.Equal(0UL, segment.GetCharLength(0));
        Assert.Equal(0UL, segment.GetCharLength(1));
        Assert.Equal(0UL, segment.GetCharLength(2));

        // Set char lengths and verify interleaving
        segment.SetCharLength(0, 5);
        segment.SetCharLength(1, 15);
        segment.SetCharLength(2, 25);

        // Byte lengths still correct
        Assert.Equal(10UL, segment.GetByteLength(0));
        Assert.Equal(20UL, segment.GetByteLength(1));
        Assert.Equal(30UL, segment.GetByteLength(2));

        // Char lengths now set
        Assert.Equal(5UL, segment.GetCharLength(0));
        Assert.Equal(15UL, segment.GetCharLength(1));
        Assert.Equal(25UL, segment.GetCharLength(2));
    }

    // --- Additional edge case: GetCharLength partial writes ---

    [Fact]
    public void GetCharLength_PartialWrites_NullForUnwrittenLines()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 10, 20, 30 });

        // Write char length only for line 0
        index.SetCharLength(0, 8);

        // Line 0 has char length
        Assert.Equal(8UL, index.GetCharLength(0));
        // Lines 1 and 2 still null (not yet written by Full_Scan)
        Assert.Null(index.GetCharLength(1));
        Assert.Null(index.GetCharLength(2));
    }
}
