using System.Collections.Concurrent;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for concurrent read safety of LineIndex.
/// Feature: unified-scan-pass, Property 5: Concurrent read safety
/// </summary>
public class ConcurrentReadSafetyPropertyTests
{
    /// <summary>
    /// Generates LinePair arrays (50–500 lines) with values spanning tier boundaries.
    /// CharLength always &lt;= ByteLength.
    /// </summary>
    private static Arbitrary<LinePair[]> LinePairArrays()
    {
        var tierByte = Gen.Choose(1, 255).Select(v => (ulong)v);
        var tierUShort = Gen.Choose(256, 65535).Select(v => (ulong)v);
        var tierUInt = Gen.Choose(65536, (int)Math.Min(4294967295L, int.MaxValue))
            .Select(v => (ulong)v);
        var tierULong = Gen.Choose(1, int.MaxValue)
            .Select(v => (ulong)v + 4294967295UL);

        var anyByteLen = Gen.OneOf(tierByte, tierUShort, tierUInt, tierULong);

        var pairGen = anyByteLen.SelectMany(byteLen =>
        {
            var charLen = Gen.Choose(0, (int)Math.Min(byteLen, (ulong)int.MaxValue))
                .Select(v => (ulong)v);
            return charLen.Select(cl => new LinePair(byteLen, cl));
        });

        var gen = Gen.Choose(50, 500)
            .SelectMany(len => Gen.ArrayOf(pairGen, len));

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 5: Concurrent read safety
    ///
    /// For any interleaving of a single writer thread appending line pairs and multiple
    /// reader threads querying the Line_Index, every reader SHALL observe either a complete,
    /// previously-written value or the absence of the line (lineIndex >= LineCount).
    /// No torn or partially-updated pair SHALL ever be observable.
    ///
    /// **Validates: Requirements 7.1, 7.2, 3.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ConcurrentReads_NeverObserveTornValues()
    {
        return Prop.ForAll(
            LinePairArrays(),
            (LinePair[] pairs) =>
            {
                var lineIndex = new LineIndex();
                var observations = new ConcurrentBag<(int LineIdx, string Method, ulong Value)>();
                var writerDone = new ManualResetEventSlim(false);

                // Start 4 reader threads
                var readerTasks = new Task[4];
                for (int r = 0; r < readerTasks.Length; r++)
                {
                    var readerRng = new Random(r + 1);
                    readerTasks[r] = Task.Run(() =>
                    {
                        while (!writerDone.IsSet)
                        {
                            var currentLineCount = lineIndex.LineCount;
                            if (currentLineCount == 0)
                            {
                                Thread.Yield();
                                continue;
                            }

                            int queryIndex = readerRng.Next(0, currentLineCount);

                            // Read GetByteLength
                            try
                            {
                                var byteLen = lineIndex.GetByteLength(queryIndex);
                                observations.Add((queryIndex, "GetByteLength", byteLen));
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                // Race on lineCount boundary — acceptable
                            }

                            // Read GetCharLength
                            try
                            {
                                var charLen = lineIndex.GetCharLength(queryIndex);
                                observations.Add((queryIndex, "GetCharLength", charLen));
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                // Race on lineCount boundary — acceptable
                            }

                            // Read GetByteOffset for small indices to keep test fast
                            if (queryIndex <= 30)
                            {
                                try
                                {
                                    var offset = lineIndex.GetByteOffset(queryIndex);
                                    observations.Add((queryIndex, "GetByteOffset", offset));
                                }
                                catch (ArgumentOutOfRangeException)
                                {
                                    // Race on lineCount boundary — acceptable
                                }
                            }

                            Thread.Yield();
                        }

                        // Final reads after writer done
                        var finalCount = lineIndex.LineCount;
                        if (finalCount > 0)
                        {
                            int idx = readerRng.Next(0, finalCount);
                            try
                            {
                                observations.Add((idx, "GetByteLength", lineIndex.GetByteLength(idx)));
                                observations.Add((idx, "GetCharLength", lineIndex.GetCharLength(idx)));
                            }
                            catch (ArgumentOutOfRangeException) { }
                        }
                    });
                }

                // Writer thread: append pairs in batches of 10
                var writerTask = Task.Run(() =>
                {
                    const int batchSize = 10;
                    for (int i = 0; i < pairs.Length; i += batchSize)
                    {
                        int count = Math.Min(batchSize, pairs.Length - i);
                        lineIndex.AppendLinePairs(pairs.AsSpan(i, count));
                        Thread.Yield();
                    }
                    writerDone.Set();
                });

                Task.WaitAll([writerTask, .. readerTasks]);

                // Validate: every observation must match expected value
                foreach (var (lineIdx, method, value) in observations)
                {
                    switch (method)
                    {
                        case "GetByteLength":
                        {
                            var expected = pairs[lineIdx].ByteLength;
                            if (value != expected)
                                return false.Label(
                                    $"Torn read: GetByteLength({lineIdx}) = {value}, expected {expected}");
                            break;
                        }
                        case "GetCharLength":
                        {
                            var expected = pairs[lineIdx].CharLength;
                            if (value != expected)
                                return false.Label(
                                    $"Torn read: GetCharLength({lineIdx}) = {value}, expected {expected}");
                            break;
                        }
                        case "GetByteOffset":
                        {
                            ulong expectedOffset = 0;
                            for (int i = 0; i < lineIdx; i++)
                                expectedOffset += pairs[i].ByteLength;
                            if (value != expectedOffset)
                                return false.Label(
                                    $"Torn read: GetByteOffset({lineIdx}) = {value}, expected {expectedOffset}");
                            break;
                        }
                    }
                }

                return true.Label(
                    $"All {observations.Count} concurrent observations consistent");
            });
    }
}
