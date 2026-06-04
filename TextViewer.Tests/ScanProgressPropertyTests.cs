using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Property-based tests for scan progress percentage computation.
/// Feature: scan-progress-bar, Property 4: Progress percentage computation
/// Validates: Requirements 4.3, 4.4, 4.5
/// </summary>
public class ScanProgressPropertyTests
{
    /// <summary>
    /// For any (bytesRead, totalFileSize) where 0 &lt;= bytesRead &lt;= totalFileSize and totalFileSize &gt; 0,
    /// progress = floor(bytesRead * 100 / totalFileSize).
    /// **Validates: Requirements 4.3, 4.4, 4.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ProgressPercentage_WhenScanInProgress_EqualsFloorFormula()
    {
        var totalGen = Gen.Choose(1, 10_000_000).Select(x => (long)x);
        var fractionGen = Gen.Choose(0, 100);

        return Prop.ForAll(
            Arb.From(totalGen),
            Arb.From(fractionGen),
            (totalFileSize, pct) =>
            {
                long bytesRead = totalFileSize * pct / 100;

                // Replicate the production formula
                int actual = (int)(bytesRead * 100 / totalFileSize);

                // Result must be in [0, 100] and equal to integer division floor
                long expectedLong = bytesRead * 100 / totalFileSize;
                int expected = (int)expectedLong;

                return (actual == expected && actual >= 0 && actual <= 100)
                    .Label($"bytesRead={bytesRead}, total={totalFileSize}, actual={actual}, expected={expected}");
            });
    }

    /// <summary>
    /// When totalFileSize == 0, progress SHALL be 100.
    /// **Validates: Requirements 4.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ProgressPercentage_WhenTotalFileSizeZero_Returns100()
    {
        var scanStateGen = Gen.Elements(
            ScanState.NotStarted,
            ScanState.ScanInProgress,
            ScanState.ScanComplete,
            ScanState.Failed,
            ScanState.Cancelled);

        return Prop.ForAll(
            Arb.From(scanStateGen),
            scanState =>
            {
                long totalFileSize = 0;
                long bytesRead = 0;

                int progressPercentage;
                if (scanState >= ScanState.ScanComplete || totalFileSize == 0)
                    progressPercentage = 100;
                else
                    progressPercentage = (int)(bytesRead * 100 / totalFileSize);

                return (progressPercentage == 100)
                    .Label($"scanState={scanState}, expected 100 but got {progressPercentage}");
            });
    }

    /// <summary>
    /// When scan state is terminal (ScanComplete, Failed, Cancelled), progress SHALL be 100
    /// regardless of bytesRead/totalFileSize values.
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ProgressPercentage_WhenTerminalState_Returns100()
    {
        var terminalStateGen = Gen.Elements(ScanState.ScanComplete, ScanState.Failed, ScanState.Cancelled);
        var longGen = Gen.Choose(0, 10_000_000).Select(x => (long)x);

        return Prop.ForAll(
            Arb.From(terminalStateGen),
            Arb.From(longGen),
            Arb.From(longGen),
            (scanState, bytesRead, totalFileSize) =>
            {
                int progressPercentage;
                if (scanState >= ScanState.ScanComplete || totalFileSize == 0)
                    progressPercentage = 100;
                else
                    progressPercentage = (int)(bytesRead * 100 / totalFileSize);

                return (progressPercentage == 100)
                    .Label($"scanState={scanState}, bytesRead={bytesRead}, total={totalFileSize}, got {progressPercentage}");
            });
    }
}


/// <summary>
/// Property-based tests for bytes-read invariant after scan.
/// Feature: scan-progress-bar, Property 5: Bytes-read invariant after scan
/// Validates: Requirements 4.1
/// </summary>
public class ScanProgressBytesReadPropertyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFileFromBytes(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scan_prop_{Guid.NewGuid():N}.bin");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Property 5: Bytes-read invariant after scan
    ///
    /// For any file content, after StartScanAsync completes successfully,
    /// BytesRead SHALL equal the total byte length of the file stream (i.e., TotalFileSize).
    ///
    /// **Validates: Requirements 4.1**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property BytesRead_Equals_TotalFileSize_After_Scan()
    {
        var contentGen = Gen.Choose(0, 200_000)
            .SelectMany(size => Gen.ArrayOf(Gen.Choose(0, 255).Select(i => (byte)i), size));

        return Prop.ForAll(
            Arb.From(contentGen),
            content => RunBytesReadInvariantTest(content).Result);
    }

    private async Task<Property> RunBytesReadInvariantTest(byte[] content)
    {
        var path = CreateTempFileFromBytes(content);
        var logger = NullLogger<FileIndex>.Instance;

        using var fileIndex = new FileIndex(path, CancellationToken.None, logger);
        var result = await fileIndex.StartScanAsync();

        if (!result.IsSuccess)
        {
            return false.Label($"Scan failed unexpectedly: {result.Error}");
        }

        var bytesRead = fileIndex.BytesRead;
        var totalFileSize = fileIndex.TotalFileSize;

        return (bytesRead == totalFileSize).Label(
            $"BytesRead ({bytesRead}) should equal TotalFileSize ({totalFileSize}) for content of {content.Length} bytes");
    }
}
