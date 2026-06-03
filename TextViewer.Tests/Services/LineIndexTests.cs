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
        index.AppendLinePairs(ReadOnlySpan<LinePair>.Empty);

        Assert.Equal(0, index.LineCount);
    }

    // --- Single-line file → one segment with one pair ---

    [Fact]
    public void SingleLineFile_OneSegment_OnePair()
    {
        var index = new LineIndex();
        index.AppendLinePairs(new LinePair[] { new(42, 40) });

        Assert.Equal(1, index.LineCount);
        Assert.Equal(42UL, index.GetByteLength(0));
    }

    // --- Tier widening at segment boundary ---

    [Fact]
    public void TierWidening_AtSegmentBoundary_CreatesNewSegment()
    {
        var dir = new SegmentDirectory();
        // Many lines fit in Byte tier, then one line requires UShort tier.
        var pairs = new LinePair[]
        {
            new(100, 100), new(100, 100), new(100, 100),
            new(100, 100), new(100, 100), new(300, 300)
        };
        dir.Append(pairs, 0);

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
        var pairs = new LinePair[12];
        pairs[0] = new LinePair(70000, 70000);
        for (int i = 1; i < 12; i++)
            pairs[i] = new LinePair(50, 50);

        dir.Append(pairs, 0);

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
        var pairs = new LinePair[] { new(300, 300), new(50, 50) };

        dir.Append(pairs, 0);

        var segments = dir.Segments;
        Assert.Single(segments);
        Assert.Equal(IntegerTier.UShort, segments[0].Tier);
    }

    // --- Segment memory == 9 + Count × 2 × TierSize ---

    [Fact]
    public void SegmentMemory_ByteTier_MatchesFormula()
    {
        var dir = new SegmentDirectory();
        dir.Append(new LinePair[] { new(10, 5), new(20, 15), new(30, 25) }, 0);

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
        dir.Append(new LinePair[] { new(300, 300), new(400, 400), new(500, 500) }, 0);

        var segment = dir.Segments[0];
        int expectedDataSize = segment.Count * 2 * (int)segment.Tier;

        Assert.Equal(IntegerTier.UShort, segment.Tier);
        Assert.Equal(expectedDataSize, segment.Data.Length);
    }

    [Fact]
    public void SegmentMemory_UIntTier_MatchesFormula()
    {
        var dir = new SegmentDirectory();
        dir.Append(new LinePair[] { new(70000, 70000), new(80000, 80000) }, 0);

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
        index.AppendLinePairs(new LinePair[] { new(10, 8), new(20, 15), new(30, 25) });

        Assert.Equal(0UL, index.GetByteOffset(0));
    }

    // --- GetByteOffset(N) == sum of Byte_Lengths[0..N-1] ---

    [Fact]
    public void GetByteOffset_N_ReturnsSumOfPreviousByteLengths()
    {
        var index = new LineIndex();
        index.AppendLinePairs(new LinePair[] { new(10, 8), new(20, 15), new(30, 25), new(40, 35) });

        Assert.Equal(0UL, index.GetByteOffset(0));
        Assert.Equal(10UL, index.GetByteOffset(1));
        Assert.Equal(30UL, index.GetByteOffset(2));  // 10 + 20
        Assert.Equal(60UL, index.GetByteOffset(3));  // 10 + 20 + 30
        Assert.Equal(100UL, index.GetByteOffset(4)); // 10 + 20 + 30 + 40 (== LineCount)
    }

    // --- GetCharLength returns value after AppendLinePairs ---

    [Fact]
    public void GetCharLength_AfterAppendLinePairs_ReturnsValue()
    {
        var index = new LineIndex();
        index.AppendLinePairs(new LinePair[] { new(10, 8), new(20, 15), new(30, 25) });

        Assert.Equal(8UL, index.GetCharLength(0));
        Assert.Equal(15UL, index.GetCharLength(1));
        Assert.Equal(25UL, index.GetCharLength(2));
    }

    // --- AppendLinePairs writes char slot without affecting byte slot ---

    [Fact]
    public void AppendLinePairs_ByteAndCharSlotsIndependent()
    {
        var index = new LineIndex();
        index.AppendLinePairs(new LinePair[] { new(100, 50), new(200, 150) });

        // Byte lengths correct
        Assert.Equal(100UL, index.GetByteLength(0));
        Assert.Equal(200UL, index.GetByteLength(1));

        // Char lengths correct
        Assert.Equal(50UL, index.GetCharLength(0));
        Assert.Equal(150UL, index.GetCharLength(1));
    }

    // --- Segment stores interleaved pairs (byteLen, charLen) ---

    [Fact]
    public void Segment_StoresInterleavedPairs()
    {
        var dir = new SegmentDirectory();
        dir.Append(new LinePair[] { new(10, 5), new(20, 15), new(30, 25) }, 0);

        var segment = dir.Segments[0];

        // Verify byte lengths stored correctly
        Assert.Equal(10UL, segment.GetByteLength(0));
        Assert.Equal(20UL, segment.GetByteLength(1));
        Assert.Equal(30UL, segment.GetByteLength(2));

        // Char lengths stored correctly
        Assert.Equal(5UL, segment.GetCharLength(0));
        Assert.Equal(15UL, segment.GetCharLength(1));
        Assert.Equal(25UL, segment.GetCharLength(2));
    }

    // --- MaxCharLength is non-nullable ulong after AppendLinePairs ---

    [Fact]
    public void MaxCharLength_NonNullable_ReturnsMaxValue()
    {
        var index = new LineIndex();
        index.AppendLinePairs(new LinePair[] { new(10, 8), new(20, 15), new(30, 25) });

        // MaxCharLength is ulong (non-nullable), returns max of char lengths
        Assert.Equal(25UL, index.MaxCharLength);
    }

    [Fact]
    public void MaxCharLength_ZeroWhenNoLinesAppended()
    {
        var index = new LineIndex();

        // No lines appended → MaxCharLength is 0 (non-nullable ulong)
        Assert.Equal(0UL, index.MaxCharLength);
    }
}
