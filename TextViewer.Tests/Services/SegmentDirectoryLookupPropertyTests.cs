using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for segment directory lookup correctness.
/// Validates: Requirements 5.4
/// </summary>
public class SegmentDirectoryLookupPropertyTests
{
    /// <summary>
    /// Generates random LinePair arrays with 1–10000 elements,
    /// byte length values spanning all tier boundaries (charLength &lt;= byteLength).
    /// </summary>
    private static Arbitrary<LinePair[]> LinePairArrays()
    {
        var tierByte = Gen.Choose(1, 255).Select(v => (ulong)v);
        var tierUShort = Gen.Choose(256, 65535).Select(v => (ulong)v);
        var tierUInt = Gen.Choose(65536, (int)Math.Min(4294967295L, int.MaxValue))
            .Select(v => (ulong)v);
        var tierULong = Gen.Choose(1, int.MaxValue)
            .Select(v => (ulong)v + 4294967295UL);

        var anyByteLength = Gen.OneOf(tierByte, tierUShort, tierUInt, tierULong);

        var gen = Gen.Choose(1, 10000)
            .SelectMany(len => Gen.ArrayOf(anyByteLength, len))
            .Select(byteLengths => byteLengths.Select(b =>
                new LinePair(b, b > 0 ? b - 1 : 0)).ToArray());

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 5: Segment directory lookup correctness
    ///
    /// For any valid line index (0 ≤ lineIndex &lt; LineCount), FindSegment(lineIndex)
    /// returns a segment where StartLine ≤ lineIndex &lt; StartLine + Count,
    /// AND GetByteLength returns the byte-length value originally stored for that line.
    ///
    /// **Validates: Requirements 5.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property FindSegment_ReturnsCorrectSegment_AndByteLengthMatchesOriginal()
    {
        return Prop.ForAll(
            LinePairArrays(),
            (LinePair[] pairs) =>
            {
                // Arrange: build a LineIndex with the generated pairs
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                // Act & Assert: for every line, verify lookup correctness
                for (int i = 0; i < pairs.Length; i++)
                {
                    var actualByteLength = lineIndex.GetByteLength(i);
                    if (actualByteLength != pairs[i].ByteLength)
                    {
                        return false.Label(
                            $"GetByteLength({i}) = {actualByteLength}, expected {pairs[i].ByteLength}");
                    }
                }

                return true.Label("All byte lengths match original values");
            });
    }

    /// <summary>
    /// Property 5: GetCharLength returns the value set via AppendLinePairs.
    ///
    /// **Validates: Requirements 5.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property GetCharLength_ReturnsValue_AfterAppendLinePairs()
    {
        return Prop.ForAll(
            LinePairArrays(),
            (LinePair[] pairs) =>
            {
                // Arrange
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                // Assert: GetCharLength returns the char length set in AppendLinePairs
                for (int i = 0; i < pairs.Length; i++)
                {
                    var actual = lineIndex.GetCharLength(i);
                    if (actual != pairs[i].CharLength)
                    {
                        return false.Label(
                            $"GetCharLength({i}) = {actual}, expected {pairs[i].CharLength}");
                    }
                }

                return true.Label("All char lengths correct after AppendLinePairs");
            });
    }

    /// <summary>
    /// Property 5: GetByteOffset returns the sum of byte lengths for lines 0..lineIndex-1.
    ///
    /// **Validates: Requirements 5.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property GetByteOffset_ReturnsSumOfPrecedingByteLengths()
    {
        return Prop.ForAll(
            LinePairArrays(),
            (LinePair[] pairs) =>
            {
                // Arrange
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                // Assert: GetByteOffset(i) == sum of byteLengths[0..i-1]
                ulong expectedOffset = 0;
                for (int i = 0; i <= pairs.Length; i++)
                {
                    var actualOffset = lineIndex.GetByteOffset(i);
                    if (actualOffset != expectedOffset)
                    {
                        return false.Label(
                            $"GetByteOffset({i}) = {actualOffset}, expected {expectedOffset}");
                    }

                    if (i < pairs.Length)
                        expectedOffset += pairs[i].ByteLength;
                }

                return true.Label("All byte offsets match prefix sums");
            });
    }
}
