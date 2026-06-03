using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Feature: unified-scan-pass, Property 6: Segment tier minimality
/// Validates: Requirements 7.3, 8.1
/// </summary>
public class SegmentTierMinimalityPropertyTests
{
    /// <summary>
    /// Generates random LinePair arrays spanning all four tier boundaries.
    /// charLength is always ≤ byteLength to match real invariant.
    /// </summary>
    private static Arbitrary<LinePair[]> LinePairArrays()
    {
        var tierByte = Gen.Choose(0, 255).Select(v => (ulong)v);
        var tierUShort = Gen.Choose(256, 65535).Select(v => (ulong)v);
        var tierUInt = Gen.Choose(65536, (int)Math.Min(4294967295L, int.MaxValue))
            .Select(v => (ulong)v);
        var tierULong = Gen.Choose(1, int.MaxValue)
            .Select(v => (ulong)v + 4294967295UL);

        var anyByteLength = Gen.OneOf(tierByte, tierUShort, tierUInt, tierULong);

        var pairGen = anyByteLength.SelectMany(bl =>
            Gen.Choose(0, (int)Math.Min(bl, (ulong)int.MaxValue))
               .Select(cl => new LinePair(bl, (ulong)cl)));

        var gen = Gen.Choose(1, 1000)
            .SelectMany(len => Gen.ArrayOf(pairGen, len));

        return Arb.From(gen);
    }

    /// <summary>
    /// Returns the maximum value representable by the given tier.
    /// </summary>
    private static ulong TierMaxValue(IntegerTier tier) => tier switch
    {
        IntegerTier.Byte => 255,
        IntegerTier.UShort => 65535,
        IntegerTier.UInt => 4294967295,
        IntegerTier.ULong => ulong.MaxValue,
        _ => throw new InvalidOperationException($"Unknown tier: {tier}")
    };

    /// <summary>
    /// Property 6: Segment tier minimality
    /// For any segment in the Line_Index, the IntegerTier SHALL be the smallest tier
    /// whose max representable value ≥ the maximum Byte_Length stored in that segment.
    ///
    /// **Validates: Requirements 7.3, 8.1**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property SegmentTier_IsMinimalForMaxByteLengthInSegment()
    {
        return Prop.ForAll(
            LinePairArrays(),
            (LinePair[] pairs) =>
            {
                var directory = new SegmentDirectory();
                directory.Append(pairs, 0);

                var segments = directory.Segments;

                for (int s = 0; s < segments.Count; s++)
                {
                    var segment = segments[s];

                    // Find the max Byte_Length stored in this segment
                    ulong maxByteLengthInSegment = 0;
                    for (int i = 0; i < segment.Count; i++)
                    {
                        var byteLen = segment.GetByteLength(i);
                        if (byteLen > maxByteLengthInSegment)
                            maxByteLengthInSegment = byteLen;
                    }

                    // Tier must equal selectTier(max Byte_Length in segment)
                    var expectedTier = SegmentDirectory.SelectTier(maxByteLengthInSegment);
                    if (segment.Tier != expectedTier)
                    {
                        return false.Label(
                            $"Segment {s} (StartLine={segment.StartLine}, Count={segment.Count}): " +
                            $"tier={segment.Tier} but expected={expectedTier} for maxByteLength={maxByteLengthInSegment}");
                    }

                    // All values must fit within the tier
                    var tierMax = TierMaxValue(segment.Tier);
                    for (int i = 0; i < segment.Count; i++)
                    {
                        var byteLen = segment.GetByteLength(i);
                        var charLen = segment.GetCharLength(i);

                        if (byteLen > tierMax)
                        {
                            return false.Label(
                                $"Segment {s}, offset {i}: byteLen={byteLen} exceeds tier max={tierMax}");
                        }

                        if (charLen > tierMax)
                        {
                            return false.Label(
                                $"Segment {s}, offset {i}: charLen={charLen} exceeds tier max={tierMax}");
                        }
                    }
                }

                return true.Label("All segments have minimal tier and all values fit");
            });
    }
}
