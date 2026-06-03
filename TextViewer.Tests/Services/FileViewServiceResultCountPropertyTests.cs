using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 2: Result count invariant
/// For any file with N scanned lines (scan complete) and any valid view request with startLine S
/// and rowCount R: if S >= N, result contains exactly 1 empty string; otherwise result contains
/// exactly min(R, N - S) rows.
///
/// **Validates: Requirements 1.4, 1.5, 1.6, 1.7**
/// </summary>
public class FileViewServiceResultCountPropertyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFileWithLines(int lineCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_prop2_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);

        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        for (int i = 0; i < lineCount; i++)
        {
            writer.Write($"Line{i}\n");
        }

        return path;
    }

    private static async Task<FileViewService> CreateServiceAndWaitForScan(string path)
    {
        var logger = NullLogger<FileViewService>.Instance;
        var service = new FileViewService(path, CancellationToken.None, logger);

        // Wait for scan to complete (ScanComplete or beyond)
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.ScanState < ScanState.ScanComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        return service;
    }

    /// <summary>
    /// Generates a tuple of (lineCount, startLine, rowCount) for testing the result count invariant.
    /// lineCount: 0–100, startLine: 0–150, rowCount: 1–50
    /// </summary>
    private static Arbitrary<(int lineCount, int startLine, int rowCount)> ResultCountArb()
    {
        var gen = Gen.Choose(0, 100).SelectMany(lineCount =>
            Gen.Choose(0, 150).SelectMany(startLine =>
                Gen.Choose(1, 50).Select(rowCount =>
                    (lineCount, startLine, rowCount))));
        return Arb.From(gen);
    }

    /// <summary>
    /// For any file with N lines (scan complete) and any valid view request with startLine S
    /// and rowCount R:
    /// - If N == 0: result contains exactly 1 empty string
    /// - If S >= N: result contains exactly 1 empty string
    /// - Otherwise: result contains exactly min(R, N - S) rows
    ///
    /// **Validates: Requirements 1.4, 1.5, 1.6, 1.7**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ResultCount_MatchesInvariant()
    {
        return Prop.ForAll(
            ResultCountArb(),
            args =>
            {
                var (lineCount, startLine, rowCount) = args;

                // Create temp file with the specified number of LF-terminated lines
                var path = CreateTempFileWithLines(lineCount);

                // Create service and wait for scan to complete
                using var service = CreateServiceAndWaitForScan(path).Result;

                // Use startCol=0, colCount=80 as specified
                var result = service.GetViewAsync(startLine, 0, rowCount, 80).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                int actualCount = result.Value.Rows.Count;

                // Compute expected count per the invariant
                int expectedCount;
                if (lineCount == 0 || startLine >= lineCount)
                {
                    expectedCount = 1; // Single empty string
                }
                else
                {
                    expectedCount = Math.Min(rowCount, lineCount - startLine);
                }

                return (actualCount == expectedCount)
                    .Label($"lineCount={lineCount}, startLine={startLine}, rowCount={rowCount}: " +
                           $"expected {expectedCount} rows, got {actualCount}");
            });
    }
}
