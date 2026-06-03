using System.Diagnostics;
using Xunit.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

public class LineIndexGetByteOffsetBugConditionBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public LineIndexGetByteOffsetBugConditionBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GetByteOffset_BenchmarkProfiles_NearEofRandomSequential()
    {
        const int lineCount = 200_000;
        var rng = new Random(12345);
        var pairs = new LinePair[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            var byteLen = (ulong)rng.Next(1, 4096);
            pairs[i] = new LinePair(byteLen, byteLen > 0 ? byteLen - 1 : 0);
        }

        var index = new LineIndex();
        index.AppendLinePairs(pairs);

        var nearEofQueries = BuildNearEofQueries(lineCount, 10_000);
        var randomQueries = BuildRandomQueries(lineCount, 10_000, 54321);
        var sequentialQueries = BuildSequentialWindowQueries(120_000, 10_000);

        var nearEof = MeasureMedianTicks(index, nearEofQueries, pairs);
        var random = MeasureMedianTicks(index, randomQueries, pairs);
        var sequential = MeasureMedianTicks(index, sequentialQueries, pairs);

        _output.WriteLine($"[GetByteOffset benchmark] lineCount={lineCount}");
        _output.WriteLine($"[GetByteOffset benchmark] near-EOF median ticks/query: {nearEof}");
        _output.WriteLine($"[GetByteOffset benchmark] random median ticks/query: {random}");
        _output.WriteLine($"[GetByteOffset benchmark] sequential median ticks/query: {sequential}");

        Assert.True(nearEof > 0);
        Assert.True(random > 0);
        Assert.True(sequential > 0);
    }

    private static long MeasureMedianTicks(LineIndex index, int[] queries, LinePair[] pairs)
    {
        var timings = new long[5];

        for (int run = 0; run < timings.Length; run++)
        {
            var sw = Stopwatch.StartNew();
            ulong checksum = 0;

            foreach (var query in queries)
            {
                var actual = index.GetByteOffset(query);
                checksum ^= actual;
            }

            sw.Stop();

            if (checksum == 0)
            {
                checksum = VerifyOneQuery(index, queries[^1], pairs);
            }

            timings[run] = sw.ElapsedTicks / queries.Length;
        }

        Array.Sort(timings);
        return timings[timings.Length / 2];
    }

    private static ulong VerifyOneQuery(LineIndex index, int lineIndex, LinePair[] pairs)
    {
        ulong expected = 0;
        for (int i = 0; i < lineIndex; i++)
        {
            expected += pairs[i].ByteLength;
        }

        var actual = index.GetByteOffset(lineIndex);
        Assert.Equal(expected, actual);
        return actual;
    }

    private static int[] BuildNearEofQueries(int lineCount, int count)
    {
        var queries = new int[count];
        int start = Math.Max(0, lineCount - count);
        for (int i = 0; i < count; i++)
        {
            queries[i] = start + i;
        }

        return queries;
    }

    private static int[] BuildRandomQueries(int lineCount, int count, int seed)
    {
        var rng = new Random(seed);
        var queries = new int[count];
        for (int i = 0; i < count; i++)
        {
            queries[i] = rng.Next(0, lineCount + 1);
        }

        return queries;
    }

    private static int[] BuildSequentialWindowQueries(int startLine, int count)
    {
        var queries = new int[count];
        for (int i = 0; i < count; i++)
        {
            queries[i] = startLine + i;
        }

        return queries;
    }
}
