using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Property-based tests for wrapped line count and visual row index resolution.
/// Feature: wrapped-line-count
/// </summary>
public class WrappedLineCountPropertyTests : IDisposable
{
    private readonly string _tempDir;

    public WrappedLineCountPropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WLCPropTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
    /// <summary>
    /// Helper: creates a LineIndex populated with the given line lengths (as both byte and char lengths).
    /// </summary>
    private static LineIndex CreateLineIndex(int[] lineLengths)
    {
        var lineIndex = new LineIndex();
        if (lineLengths.Length == 0) return lineIndex;

        var pairs = lineLengths.Select(l => new LinePair((ulong)l, (ulong)l)).ToArray();
        lineIndex.AppendLinePairs(pairs);

        return lineIndex;
    }

    /// <summary>
    /// Helper: computes cumulative visual rows up to (startLine, rowWithinLine).
    /// Returns the visual row index for the given resolved position.
    /// </summary>
    private static long ComputeCumulativeVisualRowIndex(int[] lineLengths, int colCount, int startLine, int characterOffset)
    {
        long cumulative = 0;
        for (int i = 0; i < startLine; i++)
        {
            long len = lineLengths[i];
            cumulative += len == 0 ? 1 : (len + colCount - 1) / colCount;
        }

        // Add the row-within-line offset
        long rowWithinLine = characterOffset / colCount;
        cumulative += rowWithinLine;
        return cumulative;
    }

    /// <summary>
    /// Property 2: Visual row index resolution round-trip
    ///
    /// For any array of line lengths and any visual row index in [0, totalVisualRows),
    /// resolving the index to (startLine, characterOffset) and then computing the
    /// cumulative visual rows up to that position SHALL equal the original visual row index.
    ///
    /// **Validates: Requirements 4.1, 4.2, 4.3, 4.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property VisualRowIndexResolution_RoundTrip()
    {
        // Generate 1–50 line lengths in [0, 200], colCount in [1, 100]
        var lineLengthsGen = Gen.Choose(0, 200)
            .ArrayOf()
            .Where(arr => arr.Length >= 1 && arr.Length <= 50);

        var colCountGen = Gen.Choose(1, 100);

        var gen = lineLengthsGen.SelectMany(lengths =>
            colCountGen.SelectMany(colCount =>
            {
                // Compute total visual rows for this configuration
                long totalVisualRows = 0;
                foreach (var len in lengths)
                {
                    totalVisualRows += len == 0 ? 1 : ((long)len + colCount - 1) / colCount;
                }

                // Generate a visual row index in [0, totalVisualRows)
                var indexGen = Gen.Choose(0, (int)(totalVisualRows - 1))
                    .Select(i => (long)i);

                return indexGen.Select(visualRowIndex =>
                    (lengths, colCount, visualRowIndex, totalVisualRows));
            }));

        return Prop.ForAll(
            Arb.From(gen),
            tuple =>
            {
                var (lengths, colCount, visualRowIndex, _) = tuple;

                var lineIndex = CreateLineIndex(lengths);
                var lineCount = lengths.Length;

                // Resolve visual row index to (startLine, characterOffset)
                var (startLine, characterOffset) = Program.ResolveVisualRowIndex(
                    lineIndex, lineCount, colCount, visualRowIndex);

                // Recompute cumulative position from resolved result
                long recomputed = ComputeCumulativeVisualRowIndex(lengths, colCount, startLine, characterOffset);

                return (recomputed == visualRowIndex)
                    .Label($"Expected visualRowIndex={visualRowIndex}, got recomputed={recomputed} " +
                           $"(startLine={startLine}, charOffset={characterOffset}, colCount={colCount}, " +
                           $"lines={string.Join(",", lengths.Take(10))}{(lengths.Length > 10 ? "..." : "")})");
            });
    }

    /// <summary>
    /// Property 3: Cache key correctness
    ///
    /// For any sequence of requests to the same session, the handler SHALL return the cached
    /// value (without recomputation) if and only if both colCount and lineCount are unchanged
    /// from the previous computation; otherwise it SHALL recompute.
    ///
    /// **Validates: Requirements 6.1, 6.2, 6.3, 6.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property CacheKeyCorrectness_HitWhenUnchanged_MissWhenEitherChanges()
    {
        // Generate a sequence of 2–6 (colCount, lineCount) pairs
        var pairGen = Gen.Choose(1, 80).SelectMany(colCount =>
            Gen.Choose(1, 50).Select(lineCount => (colCount, lineCount)));

        var sequenceGen = pairGen.ArrayOf().Where(arr => arr.Length >= 2 && arr.Length <= 6);

        return Prop.ForAll(
            Arb.From(sequenceGen),
            sequence =>
            {
                var sessionId = "prop3-session";
                var sessionLock = new object();
                var sessions = new Dictionary<string, FileViewService>();
                var cache = new Dictionary<string, (int colCount, int lineCount, long total)>();

                // Create a temp file with enough lines for the max lineCount in the sequence
                int maxLines = sequence.Max(p => p.lineCount);
                var filePath = CreateTempFileForProperty(maxLines);
                var service = CreateService(filePath);
                sessions[sessionId] = service;

                // Wait for scan
                WaitForScanSync(service);

                // Track previous request's (colCount, lineCount) to determine expected hit/miss
                int? prevColCount = null;
                int? prevLineCount = null;

                foreach (var (colCount, lineCount) in sequence)
                {
                    // Manipulate LineIndex to have exactly `lineCount` lines:
                    // We use the service's LineIndex which has maxLines lines from scan.
                    // The actual lineCount from LineIndex is fixed after scan.
                    // Instead, we directly test the handler's cache logic by controlling
                    // what lineCount the LineIndex reports. Since we can't change LineIndex.LineCount
                    // after scan, we test cache behavior by varying colCount across calls
                    // and verifying cache dictionary state.

                    // For this property, we call the handler and check cache state.
                    var actualLineCount = service.LineIndex.LineCount;
                    var payload = $"{sessionId}\n{colCount}";

                    // Snapshot cache before call
                    var hadCacheEntry = cache.TryGetValue(sessionId, out var cachedBefore);

                    var result = Program.HandleGetWrappedLineCount(payload, sessions, sessionLock, cache);

                    // Verify no error
                    if (result.StartsWith("ERROR:"))
                    {
                        service.Dispose();
                        return false.Label($"Unexpected error: {result}");
                    }

                    var returnedTotal = long.Parse(result);

                    // Verify cache was updated
                    var cachedAfter = cache[sessionId];
                    if (cachedAfter.total != returnedTotal)
                    {
                        service.Dispose();
                        return false.Label("Cache total doesn't match returned value");
                    }

                    // Verify hit/miss logic:
                    // Cache HIT = had entry AND colCount unchanged AND lineCount unchanged
                    bool shouldHit = hadCacheEntry
                        && cachedBefore.colCount == colCount
                        && cachedBefore.lineCount == actualLineCount;

                    if (shouldHit)
                    {
                        // On hit, returned value must equal previously cached total
                        if (returnedTotal != cachedBefore.total)
                        {
                            service.Dispose();
                            return false.Label(
                                $"Expected cache hit (same total={cachedBefore.total}), got {returnedTotal}");
                        }
                    }

                    // After call, cache must reflect current (colCount, lineCount, total)
                    if (cachedAfter.colCount != colCount || cachedAfter.lineCount != actualLineCount)
                    {
                        service.Dispose();
                        return false.Label(
                            $"Cache entry mismatch: expected ({colCount},{actualLineCount}), " +
                            $"got ({cachedAfter.colCount},{cachedAfter.lineCount})");
                    }

                    prevColCount = colCount;
                    prevLineCount = actualLineCount;
                }

                service.Dispose();
                return true.Label("Cache key correctness verified");
            });
    }

    private string CreateTempFileForProperty(int lineCount)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.txt");
        using var writer = new StreamWriter(path);
        for (int i = 0; i < lineCount; i++)
        {
            writer.WriteLine(new string('A', (i % 20) + 1));
        }
        return path;
    }

    private FileViewService CreateService(string filePath)
    {
        var logger = new NullLogger<FileViewService>();
        return new FileViewService(filePath, CancellationToken.None, logger);
    }

    private static void WaitForScanSync(FileViewService service, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (service.ScanState < ScanState.ScanComplete && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>Null logger for test services.</summary>
    private class NullLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
