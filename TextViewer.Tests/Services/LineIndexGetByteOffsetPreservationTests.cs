using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

public class LineIndexGetByteOffsetPreservationTests
{
    private static Arbitrary<ulong[]> ByteLengthSpans()
    {
        var gen = Gen.Choose(1, 500)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100_000).Select(v => (ulong)v),
                    len));

        return Arb.From(gen);
    }

    [Property(MaxTest = 10)]
    public Property GetByteOffset_Zero_And_LineCount_Are_Correct()
    {
        return Prop.ForAll(
            ByteLengthSpans(),
            (ulong[] byteLengths) =>
            {
                var index = new LineIndex();
                index.AppendByteLengths(byteLengths);

                ulong expectedFileSize = 0;
                for (int i = 0; i < byteLengths.Length; i++)
                {
                    expectedFileSize += byteLengths[i];
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
            ByteLengthSpans(),
            (ulong[] byteLengths) =>
            {
                var index = new LineIndex();
                index.AppendByteLengths(byteLengths);

                ulong running = 0;
                for (int i = 0; i <= byteLengths.Length; i++)
                {
                    var actual = index.GetByteOffset(i);
                    if (actual != running)
                    {
                        return false.Label(
                            $"GetByteOffset({i})={actual}, expected={running}");
                    }

                    if (i < byteLengths.Length)
                        running += byteLengths[i];
                }

                return true.Label("All offsets match cumulative sums");
            });
    }

    [Fact]
    public void GetByteOffset_Is_Correct_At_SegmentBoundaries()
    {
        // Forces multiple segments due to tier widening transitions.
        var byteLengths = new ulong[] { 1, 2, 255, 256, 257, 65_535, 65_536, 70_000, 4_294_967_296 };
        var index = new LineIndex();
        index.AppendByteLengths(byteLengths);

        ulong running = 0;
        for (int i = 0; i <= byteLengths.Length; i++)
        {
            Assert.Equal(running, index.GetByteOffset(i));
            if (i < byteLengths.Length)
                running += byteLengths[i];
        }
    }

    [Fact]
    public void Clear_Resets_Offsets_And_LineCount()
    {
        var index = new LineIndex();
        index.AppendByteLengths(new ulong[] { 10, 20, 30 });

        Assert.Equal(60UL, index.GetByteOffset(index.LineCount));
        Assert.Equal(3, index.LineCount);

        index.Clear();

        Assert.Equal(0, index.LineCount);
        Assert.Equal(0UL, index.GetByteOffset(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.GetByteOffset(1));
    }
}