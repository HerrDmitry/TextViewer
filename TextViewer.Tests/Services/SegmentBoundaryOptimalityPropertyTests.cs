using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for segment boundary optimality.
/// Feature: unified-scan-pass, Property 7: Segment boundary optimality
/// Validates: Requirements 8.2, 8.3
/// </summary>
public class SegmentBoundaryOptimalityPropertyTests
{
    private const int MetadataCost = 9;

    /// <summary>
    /// Generates LinePair arrays with tier-crossing patterns to exercise segment boundary decisions.
    /// ByteLength spans all four tiers; CharLength ≤ ByteLength (guaranteed by design).
    /// </summary>
    private static Arbitrary<LinePair[]> TierCrossingLinePairs()
    {
        var byteTier = Gen.Choose(0, 255).Select(v => (ulong)v);
        var ushortTier = Gen.Choose(256, 65535).Select(v => (ulong)v);
        var uintTier = Gen.Choose(65536, int.MaxValue).Select(v => (ulong)v)
            .Or(Gen.Constant((ulong)4294967295));
        var ulongTier = Gen.Constant((ulong)4294967296)
            .Or(Gen.Constant(ulong.MaxValue));

        var anyTierValue = Gen.OneOf(byteTier, ushortTier, uintTier, ulongTier);

        // CharLength ≤ ByteLength: generate as fraction of ByteLength
        var pairGen = anyTierValue.SelectMany(byteLen =>
            Gen.Choose(0, (int)Math.Min(byteLen, (ulong)int.MaxValue))
                .Select(charLen => new LinePair(byteLen, (ulong)charLen)));

        var gen = Gen.Choose(1, 200)
            .SelectMany(len => Gen.ArrayOf(pairGen, len));

        return Arb.From(gen);
    }

    /// <summary>
    /// Computes the memory cost of a single segment.
    /// Memory = MetadataCost (9) + Count × 2 × TierSize
    /// </summary>
    private static long SegmentMemory(int count, IntegerTier tier)
    {
        return MetadataCost + (long)count * 2 * (int)tier;
    }

    /// <summary>
    /// Selects the smallest tier that can hold the given value.
    /// Mirrors SegmentDirectory.SelectTier.
    /// </summary>
    private static IntegerTier SelectTier(ulong value)
    {
        if (value <= 255) return IntegerTier.Byte;
        if (value <= 65535) return IntegerTier.UShort;
        if (value <= 4294967295) return IntegerTier.UInt;
        return IntegerTier.ULong;
    }

    /// <summary>
    /// Gets the maximum byte length value stored in a segment.
    /// </summary>
    private static ulong GetMaxByteLengthInSegment(Segment segment)
    {
        ulong max = 0;
        for (int i = 0; i < segment.Count; i++)
        {
            var val = segment.GetByteLength(i);
            if (val > max) max = val;
        }
        return max;
    }

    /// <summary>
    /// Property 7: Segment boundary optimality (merge check)
    /// 
    /// For any pair of adjacent segments, merging them into one SHALL NOT reduce
    /// total memory consumption. After Optimize(), no profitable merge exists.
    ///
    /// **Validates: Requirements 8.2, 8.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MergingAdjacentSegments_DoesNotReduceMemory()
    {
        return Prop.ForAll(
            TierCrossingLinePairs(),
            (LinePair[] pairs) =>
            {
                var directory = new SegmentDirectory();
                directory.Append(pairs, 0);
                directory.Optimize();

                var segments = directory.Segments;

                if (segments.Count < 2)
                    return true.Label("Fewer than 2 segments — merge check trivially passes");

                for (int i = 0; i < segments.Count - 1; i++)
                {
                    var seg1 = segments[i];
                    var seg2 = segments[i + 1];

                    long currentMemory = SegmentMemory(seg1.Count, seg1.Tier)
                                       + SegmentMemory(seg2.Count, seg2.Tier);

                    ulong max1 = GetMaxByteLengthInSegment(seg1);
                    ulong max2 = GetMaxByteLengthInSegment(seg2);
                    var mergedTier = SelectTier(Math.Max(max1, max2));

                    long mergedMemory = SegmentMemory(seg1.Count + seg2.Count, mergedTier);

                    if (mergedMemory < currentMemory)
                    {
                        return false.Label(
                            $"Merge of segments {i} and {i + 1} would save memory: " +
                            $"current={currentMemory}, merged={mergedMemory}, " +
                            $"seg1(count={seg1.Count}, tier={seg1.Tier}), " +
                            $"seg2(count={seg2.Count}, tier={seg2.Tier}), " +
                            $"mergedTier={mergedTier}");
                    }
                }

                return true.Label("No profitable merge exists");
            });
    }

    /// <summary>
    /// Property 7: Segment boundary optimality (split check)
    /// 
    /// For any single segment, splitting it at any point SHALL NOT reduce
    /// total memory consumption. After Optimize(), no profitable split exists.
    ///
    /// **Validates: Requirements 8.2, 8.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property SplittingAnySegment_DoesNotReduceMemory()
    {
        return Prop.ForAll(
            TierCrossingLinePairs(),
            (LinePair[] pairs) =>
            {
                var directory = new SegmentDirectory();
                directory.Append(pairs, 0);
                directory.Optimize();

                var segments = directory.Segments;

                for (int s = 0; s < segments.Count; s++)
                {
                    var segment = segments[s];

                    if (segment.Count < 2)
                        continue;

                    long currentMemory = SegmentMemory(segment.Count, segment.Tier);

                    for (int splitAt = 1; splitAt < segment.Count; splitAt++)
                    {
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

                        long splitMemory = SegmentMemory(splitAt, leftTier)
                                         + SegmentMemory(segment.Count - splitAt, rightTier);

                        if (splitMemory < currentMemory)
                        {
                            return false.Label(
                                $"Split of segment {s} at position {splitAt} would save memory: " +
                                $"current={currentMemory}, split={splitMemory}, " +
                                $"segment(count={segment.Count}, tier={segment.Tier}), " +
                                $"leftTier={leftTier}, rightTier={rightTier}");
                        }
                    }
                }

                return true.Label("No profitable split exists");
            });
    }
}
