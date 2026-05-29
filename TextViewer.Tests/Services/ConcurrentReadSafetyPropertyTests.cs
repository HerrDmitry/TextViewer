using System.Collections.Concurrent;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for concurrent read safety (no torn values).
/// Validates: Requirements 4.1, 4.2, 4.3
/// </summary>
public class ConcurrentReadSafetyPropertyTests
{
    /// <summary>
    /// Generates random ulong[] byteLengths (100–1000 lines) with values spanning tier boundaries.
    /// </summary>
    private static Arbitrary<ulong[]> ByteLengthArrays()
    {
        var tierByte = Gen.Choose(1, 255).Select(v => (ulong)v);
        var tierUShort = Gen.Choose(256, 65535).Select(v => (ulong)v);
        var tierUInt = Gen.Choose(65536, (int)Math.Min(4294967295L, int.MaxValue))
            .Select(v => (ulong)v);
        var tierULong = Gen.Choose(1, int.MaxValue)
            .Select(v => (ulong)v + 4294967295UL);

        var anyValue = Gen.OneOf(tierByte, tierUShort, tierUInt, tierULong);

        var gen = Gen.Choose(100, 1000)
            .SelectMany(len => Gen.ArrayOf(anyValue, len))
            .Select(arr => arr.Select(v => v).ToArray());

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates char lengths that are always &lt;= the corresponding byte length.
    /// </summary>
    private static ulong[] GenerateCharLengths(ulong[] byteLengths, Random rng)
    {
        var charLengths = new ulong[byteLengths.Length];
        for (int i = 0; i < byteLengths.Length; i++)
        {
            // Char length is always <= byte length
            charLengths[i] = (ulong)rng.NextInt64(0, (long)Math.Min(byteLengths[i], (ulong)long.MaxValue) + 1);
        }
        return charLengths;
    }

    /// <summary>
    /// Record of an observation made by a reader thread.
    /// </summary>
    private record struct Observation(
        int LineIndex,
        string Method,
        ulong? Value,
        bool ThrewException);

    /// <summary>
    /// Property 7: Concurrent read safety (no torn values)
    /// For any interleaving of a single writer thread appending/updating Line_Index pairs
    /// and multiple reader threads querying GetByteLength, GetCharLength, or GetByteOffset,
    /// every reader SHALL observe either the complete previous value or the complete new value
    /// — never a partially-written intermediate state.
    ///
    /// Validates: Requirements 4.1, 4.2, 4.3
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ConcurrentReads_NeverObserveTornValues()
    {
        return Prop.ForAll(
            ByteLengthArrays(),
            (ulong[] byteLengths) =>
            {
                var lineIndex = new LineIndex();
                var observations = new ConcurrentBag<Observation>();
                var writerDone = new ManualResetEventSlim(false);
                var rng = new Random(42);
                var charLengths = GenerateCharLengths(byteLengths, rng);

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
                                observations.Add(new Observation(queryIndex, "GetByteLength", byteLen, false));
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                // Race: lineCount changed between read and query — acceptable
                                observations.Add(new Observation(queryIndex, "GetByteLength", null, true));
                            }

                            // Read GetCharLength
                            try
                            {
                                var charLen = lineIndex.GetCharLength(queryIndex);
                                observations.Add(new Observation(queryIndex, "GetCharLength", charLen, false));
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                observations.Add(new Observation(queryIndex, "GetCharLength", null, true));
                            }

                            // Read GetByteOffset for a small index to keep it fast
                            if (queryIndex <= 50)
                            {
                                try
                                {
                                    var offset = lineIndex.GetByteOffset(queryIndex);
                                    observations.Add(new Observation(queryIndex, "GetByteOffset", offset, false));
                                }
                                catch (ArgumentOutOfRangeException)
                                {
                                    observations.Add(new Observation(queryIndex, "GetByteOffset", null, true));
                                }
                            }

                            Thread.Yield();
                        }

                        // One final read pass after writer is done
                        var finalLineCount = lineIndex.LineCount;
                        if (finalLineCount > 0)
                        {
                            int idx = readerRng.Next(0, finalLineCount);
                            try
                            {
                                var byteLen = lineIndex.GetByteLength(idx);
                                observations.Add(new Observation(idx, "GetByteLength", byteLen, false));
                            }
                            catch (ArgumentOutOfRangeException) { }

                            try
                            {
                                var charLen = lineIndex.GetCharLength(idx);
                                observations.Add(new Observation(idx, "GetCharLength", charLen, false));
                            }
                            catch (ArgumentOutOfRangeException) { }
                        }
                    });
                }

                // Writer thread: append byteLengths in batches, then write char lengths
                var writerTask = Task.Run(() =>
                {
                    // Phase 1: Append byte lengths in batches of 10
                    int batchSize = 10;
                    for (int i = 0; i < byteLengths.Length; i += batchSize)
                    {
                        int count = Math.Min(batchSize, byteLengths.Length - i);
                        var batch = byteLengths.AsSpan(i, count);
                        lineIndex.AppendByteLengths(batch);
                        Thread.Yield(); // Give readers a chance to interleave
                    }

                    // Phase 2: Write char lengths sequentially
                    for (int i = 0; i < charLengths.Length; i++)
                    {
                        lineIndex.SetCharLength(i, charLengths[i]);
                        if (i % 5 == 0)
                            Thread.Yield(); // Give readers a chance to interleave
                    }

                    writerDone.Set();
                });

                // Wait for all tasks to complete
                Task.WaitAll([writerTask, .. readerTasks]);

                // Validate observations
                foreach (var obs in observations)
                {
                    if (obs.ThrewException)
                        continue; // Race condition on lineCount boundary — acceptable

                    switch (obs.Method)
                    {
                        case "GetByteLength":
                        {
                            // Every observed byte length must match the original value
                            var expected = byteLengths[obs.LineIndex];
                            if (obs.Value != expected)
                            {
                                return false.Label(
                                    $"Torn read: GetByteLength({obs.LineIndex}) returned {obs.Value} " +
                                    $"but expected {expected}");
                            }
                            break;
                        }

                        case "GetCharLength":
                        {
                            // GetCharLength must be null (not yet written) or the final value
                            if (obs.Value.HasValue)
                            {
                                var expectedChar = charLengths[obs.LineIndex];
                                if (obs.Value.Value != expectedChar)
                                {
                                    return false.Label(
                                        $"Torn read: GetCharLength({obs.LineIndex}) returned {obs.Value.Value} " +
                                        $"but expected null or {expectedChar}");
                                }
                            }
                            // null is acceptable (not yet written by Full_Scan)
                            break;
                        }

                        case "GetByteOffset":
                        {
                            // GetByteOffset must be consistent: sum of byte lengths [0..lineIndex-1]
                            ulong expectedOffset = 0;
                            for (int i = 0; i < obs.LineIndex; i++)
                            {
                                expectedOffset += byteLengths[i];
                            }
                            if (obs.Value != expectedOffset)
                            {
                                return false.Label(
                                    $"Torn read: GetByteOffset({obs.LineIndex}) returned {obs.Value} " +
                                    $"but expected {expectedOffset}");
                            }
                            break;
                        }
                    }
                }

                return true.Label(
                    $"All {observations.Count} observations consistent (no torn reads)");
            });
    }
}
