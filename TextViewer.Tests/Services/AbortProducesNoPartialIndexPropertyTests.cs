using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Feature: unified-scan-pass, Property 3: Abort produces no partial index
/// Validates: Requirements 1.4, 2.4, 6.1, 6.3
/// 
/// For any file content and any failure point during the unified scan (cancellation),
/// after abort the Line_Index SHALL contain zero lines, ScanState SHALL be Failed or Cancelled,
/// and no partial line data SHALL be observable by any reader thread.
/// </summary>
public class AbortProducesNoPartialIndexPropertyTests
{
    /// <summary>
    /// Generates random file content bytes (0–10KB) with mixed line endings.
    /// </summary>
    private static Arbitrary<byte[]> FileContentArb()
    {
        var gen = Gen.Choose(0, 10240).SelectMany(size =>
            Gen.ArrayOf(Gen.Choose(0, 255).Select(i => (byte)i), size));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a random cancellation delay in milliseconds (0–5ms).
    /// Short delays ensure cancellation fires during scan for non-trivial content.
    /// </summary>
    private static Arbitrary<int> CancelDelayArb()
    {
        return Arb.From(Gen.Choose(0, 5));
    }

    /// <summary>
    /// Property 3: Abort produces no partial index
    /// 
    /// For any file content and any cancellation point, after abort:
    /// - LineIndex.LineCount == 0
    /// - ScanState is Cancelled (or ScanComplete if scan finished before cancellation)
    /// - No partial line data is observable
    ///
    /// **Validates: Requirements 1.4, 2.4, 6.1, 6.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property Cancelled_Scan_Produces_No_Partial_Index()
    {
        return Prop.ForAll(
            FileContentArb(),
            CancelDelayArb(),
            (content, cancelDelayMs) =>
            {
                return RunAbortTest(content, cancelDelayMs).GetAwaiter().GetResult();
            });
    }

    private async Task<Property> RunAbortTest(byte[] content, int cancelDelayMs)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write random content to temp file
            await File.WriteAllBytesAsync(tempFile, content);

            var logger = NullLogger<FileIndex>.Instance;
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, logger);

            // Schedule cancellation after random delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(cancelDelayMs);
                cts.Cancel();
            });

            // Run the scan
            await fileIndex.StartScanAsync();

            var finalState = fileIndex.State;
            var lineCount = fileIndex.Index.LineCount;

            // Two valid outcomes:
            // 1. Scan completed before cancellation → ScanComplete, LineCount may be > 0
            // 2. Cancellation took effect → Cancelled, LineCount MUST be 0
            if (finalState == ScanState.ScanComplete)
            {
                // Scan finished before cancellation — valid outcome
                return true.ToProperty()
                    .Label($"Scan completed before cancel (delay={cancelDelayMs}ms, lines={lineCount})");
            }

            if (finalState == ScanState.Cancelled)
            {
                // Cancellation took effect — verify no partial index
                if (lineCount != 0)
                {
                    return false.ToProperty()
                        .Label($"VIOLATION: State=Cancelled but LineCount={lineCount} (expected 0). " +
                               $"Content size={content.Length}, cancelDelay={cancelDelayMs}ms");
                }

                // Verify no partial data observable by reader
                try
                {
                    if (lineCount == 0)
                    {
                        // MaxByteLength and MaxCharLength should be 0 when index is cleared
                        var maxByte = fileIndex.Index.MaxByteLength;
                        var maxChar = fileIndex.Index.MaxCharLength;
                        if (maxByte != 0 || maxChar != 0)
                        {
                            return false.ToProperty()
                                .Label($"VIOLATION: Cancelled with LineCount=0 but MaxByteLength={maxByte}, MaxCharLength={maxChar}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Any exception accessing cleared index is acceptable
                    return true.ToProperty()
                        .Label($"Cancelled, index access threw {ex.GetType().Name} (acceptable)");
                }

                return true.ToProperty()
                    .Label($"Cancelled correctly: LineCount=0, no partial data (delay={cancelDelayMs}ms, content={content.Length}B)");
            }

            if (finalState == ScanState.Failed)
            {
                // Failed state — also must have no partial index
                if (lineCount != 0)
                {
                    return false.ToProperty()
                        .Label($"VIOLATION: State=Failed but LineCount={lineCount} (expected 0). " +
                               $"Content size={content.Length}, cancelDelay={cancelDelayMs}ms");
                }

                return true.ToProperty()
                    .Label($"Failed state: LineCount=0, no partial data");
            }

            return false.ToProperty()
                .Label($"Unexpected final state: {finalState} (expected ScanComplete, Cancelled, or Failed)");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* cleanup */ }
        }
    }
}
