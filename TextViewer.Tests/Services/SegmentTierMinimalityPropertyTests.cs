using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for segment tier minimality.
/// Validates: Requirements 4.4, 5.1
/// </summary>
public class SegmentTierMinimalityPropertyTests
{
    /// <summary>
    /// Generates random ulong values spanning all four tier boundaries:
    /// Byte (0–255), UShort (256–65535), UInt (65536–4294967295), ULong (>4294967295)
    /// </summary>
    private static Arbitrary<ulong[]> ByteLengthArrays()
    {
        var tierByte = Gen.Choose(0, 255).Select(v => (ulong)v);
        var tierUShort = Gen.Choose(256, 65535).Select(v => (ulong)v);
        var tierUInt = Gen.Choose(65536, (int)Math.Min(4294967295L, int.MaxValue))
            .Select(v => (ulong)v);
        var tierULong = Gen.Choose(1, int.MaxValue)
            .Select(v => (ulong)v + 4294967295UL);

        var anyValue = Gen.OneOf(tierByte, tierUShort, tierUInt, tierULong);

        var gen = Gen.Choose(0, 1000)
            .SelectMany(len => Gen.ArrayOf(anyValue, len))
            .Select(arr => arr.Select(v => v).ToArray());

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
    /// Property 3: Segment tier minimality
    /// For any segment in the Line_Index, the IntegerTier of that segment SHALL be the
    /// smallest tier whose maximum value is >= the maximum Byte_Length stored in that segment.
    /// Since Char_Length ≤ Byte_Length for every line, both values in every pair are guaranteed
    /// to fit within the selected tier.
    ///
    /// Validates: Requirements 4.4, 5.1
    /// </summary>
    [Property(MaxTest = 10)]
    public Property SegmentTier_IsMinimalForMaxByteLengthInSegment()
    {
        return Prop.ForAll(
            ByteLengthArrays(),
            (ulong[] byteLengths) =>
            {
                if (byteLengths.Length == 0)
                    return true.Label("Empty array — no segments to check");

                var directory = new SegmentDirectory();
                directory.Append(byteLengths, 0);

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

                    // Assert: segment tier == selectTier(max Byte_Length in segment)
                    var expectedTier = SegmentDirectory.SelectTier(maxByteLengthInSegment);
                    if (segment.Tier != expectedTier)
                    {
                        return false.Label(
                            $"Segment {s} (StartLine={segment.StartLine}, Count={segment.Count}): " +
                            $"tier={segment.Tier} but expected={expectedTier} for maxByteLength={maxByteLengthInSegment}");
                    }

                    // Assert: both values in every pair fit within the selected tier
                    var tierMax = TierMaxValue(segment.Tier);
                    for (int i = 0; i < segment.Count; i++)
                    {
                        var byteLen = segment.GetByteLength(i);
                        var charLen = segment.GetCharLength(i);

                        if (byteLen > tierMax)
                        {
                            return false.Label(
                                $"Segment {s}, line offset {i}: byteLen={byteLen} exceeds tier max={tierMax} (tier={segment.Tier})");
                        }

                        if (charLen > tierMax)
                        {
                            return false.Label(
                                $"Segment {s}, line offset {i}: charLen={charLen} exceeds tier max={tierMax} (tier={segment.Tier})");
                        }
                    }
                }

                return true.Label("All segments have minimal tier and all values fit");
            });
    }
}
