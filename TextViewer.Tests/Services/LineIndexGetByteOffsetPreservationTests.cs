using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

public class LineIndexGetByteOffsetPreservationTests
{
    private static Arbitrary<LinePair[]> LinePairSpans()
    {
        var gen = Gen.Choose(1, 500)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100_000).Select(v => (ulong)v),
                    len))
            .Select(byteLengths => byteLengths.Select(b =>
                new LinePair(b, b > 0 ? b - 1 : 0)).ToArray());

        return Arb.From(gen);
    }

    [Property(MaxTest = 10)]
    public Property GetByteOffset_Zero_And_LineCount_Are_Correct()
    {
        return Prop.ForAll(
            LinePairSpans(),
            (LinePair[] pairs) =>
            {
                var index = new LineIndex();
                index.AppendLinePairs(pairs);

                ulong expectedFileSize = 0;
                for (int i = 0; i < pairs.Length; i++)
                {
                    expectedFileSize += pairs[i].ByteLength;
                }

                var offsetZero = index.GetByteOffset(0);
                var offsetLineCount = index.GetByteOffset(index.LineCount);

                return (offsetZero == 0UL && offsetLineCount == expectedFileSize).Label(
                    $"GetByteOffset(0)={offsetZero}, GetByteOffset(LineCount)={offsetLineCount}, expectedFileSize={expectedFileSize}");
            });
    }

    [Property(MaxTest = 10)]
    public Property GetByteOffset_Equals_CumulativeSum_For_All_Indices()
    {
        return Prop.ForAll(
            LinePairSpans(),
            (LinePair[] pairs) =>
            {
                var index = new LineIndex();
                index.AppendLinePairs(pairs);

                ulong running = 0;
                for (int i = 0; i <= pairs.Length; i++)
                {
                    var actual = index.GetByteOffset(i);
                    if (actual != running)
                    {
                        return false.Label(
                            $"GetByteOffset({i})={actual}, expected={running}");
                    }

                    if (i < pairs.Length)
                        running += pairs[i].ByteLength;
                }

                return true.Label("All offsets match cumulative sums");
            });
    }

    [Fact]
    public void GetByteOffset_Is_Correct_At_SegmentBoundaries()
    {
        // Forces multiple segments due to tier widening transitions.
        var pairs = new LinePair[]
        {
            new(1, 1), new(2, 2), new(255, 255), new(256, 256),
            new(257, 257), new(65_535, 65_535), new(65_536, 65_536),
            new(70_000, 70_000), new(4_294_967_296, 4_294_967_296)
        };
        var index = new LineIndex();
        index.AppendLinePairs(pairs);

        ulong running = 0;
        for (int i = 0; i <= pairs.Length; i++)
        {
            Assert.Equal(running, index.GetByteOffset(i));
            if (i < pairs.Length)
                running += pairs[i].ByteLength;
        }
    }

    [Fact]
    public void Clear_Resets_Offsets_And_LineCount()
    {
        var index = new LineIndex();
        index.AppendLinePairs(new LinePair[] { new(10, 8), new(20, 15), new(30, 25) });

        Assert.Equal(60UL, index.GetByteOffset(index.LineCount));
        Assert.Equal(3, index.LineCount);

        index.Clear();

        Assert.Equal(0, index.LineCount);
        Assert.Equal(0UL, index.GetByteOffset(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.GetByteOffset(1));
    }
}
