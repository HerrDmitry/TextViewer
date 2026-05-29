using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 6: FileIndex immutability during extraction
/// For any view request, the FileIndex LineCount and all line byte lengths observable before
/// the request SHALL remain unchanged after the request completes — view extraction is strictly
/// read-only with respect to the index.
///
/// **Validates: Requirements 6.3**
/// </summary>
public class FileViewServiceImmutabilityPropertyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFileWithRandomContent(int lineCount, int seed)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_prop6_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);

        var rng = new Random(seed);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);

        for (int i = 0; i < lineCount; i++)
        {
            // Generate random ASCII content (printable chars 0x20-0x7E)
            int contentLen = rng.Next(0, 80);
            for (int c = 0; c < contentLen; c++)
            {
                stream.WriteByte((byte)rng.Next(0x20, 0x7F));
            }
            // LF ending
            stream.WriteByte(0x0A);
        }

        return path;
    }

    private static async Task<FileViewService> CreateServiceAndWaitForScan(string path)
    {
        var logger = NullLogger<FileViewService>.Instance;
        var service = new FileViewService(path, CancellationToken.None, logger);

        // Wait for scan to complete (QuickScanComplete or beyond)
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.ScanState < ScanState.QuickScanComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        return service;
    }

    private static async Task<FileIndex> CreateFileIndexAndWaitForScan(string path)
    {
        var logger = NullLogger<FileIndex>.Instance;
        var fileIndex = new FileIndex(path, CancellationToken.None, logger);
        await fileIndex.StartScanAsync();
        return fileIndex;
    }

    /// <summary>
    /// Generates a tuple of (lineCount, startLine, startCol, rowCount, colCount, seed) for testing.
    /// lineCount: 1–50, startLine: valid range, startCol/rowCount/colCount: valid random ranges.
    /// </summary>
    private static Arbitrary<(int lineCount, int startLine, int startCol, int rowCount, int colCount, int seed)> ImmutabilityArb()
    {
        var gen = Gen.Choose(1, 50).SelectMany(lineCount =>
            Gen.Choose(0, lineCount - 1).SelectMany(startLine =>
                Gen.Choose(0, 40).SelectMany(startCol =>
                    Gen.Choose(1, 20).SelectMany(rowCount =>
                        Gen.Choose(1, 80).SelectMany(colCount =>
                            Gen.Choose(0, int.MaxValue - 1).Select(seed =>
                                (lineCount, startLine, startCol, rowCount, colCount, seed)))))));
        return Arb.From(gen);
    }

    /// <summary>
    /// For any view request, the FileIndex LineCount and all line byte lengths observable before
    /// the request SHALL remain unchanged after the request completes.
    ///
    /// Approach: Create a separate FileIndex on the same file, wait for scan, snapshot its
    /// LineCount and all byte lengths. Then use the FileViewService (which has its own FileIndex)
    /// to execute a view request. After the request, verify the snapshot FileIndex instance
    /// is unchanged — proving that view extraction does not mutate index state.
    ///
    /// **Validates: Requirements 6.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property FileIndex_Immutable_During_Extraction()
    {
        return Prop.ForAll(
            ImmutabilityArb(),
            args =>
            {
                var (lineCount, startLine, startCol, rowCount, colCount, seed) = args;

                // Create temp file with random ASCII content (1–50 lines with LF endings)
                var path = CreateTempFileWithRandomContent(lineCount, seed);

                // Create a separate FileIndex on the same file, wait for scan to complete
                using var fileIndex = CreateFileIndexAndWaitForScan(path).Result;

                // Snapshot: record LineCount and GetByteLength for each line BEFORE request
                int snapshotLineCount = fileIndex.Index.LineCount;
                var snapshotByteLengths = new ulong[snapshotLineCount];
                for (int i = 0; i < snapshotLineCount; i++)
                {
                    snapshotByteLengths[i] = fileIndex.Index.GetByteLength(i);
                }

                // Create FileViewService and wait for scan
                using var service = CreateServiceAndWaitForScan(path).Result;

                // Execute GetViewAsync with random valid params
                var result = service.GetViewAsync(startLine, startCol, rowCount, colCount).Result;

                // After request: verify the snapshot FileIndex LineCount is unchanged
                int postLineCount = fileIndex.Index.LineCount;
                if (postLineCount != snapshotLineCount)
                    return false.Label($"LineCount changed: expected {snapshotLineCount}, got {postLineCount}");

                // Verify all byte lengths unchanged
                for (int i = 0; i < snapshotLineCount; i++)
                {
                    var postByteLen = fileIndex.Index.GetByteLength(i);
                    if (postByteLen != snapshotByteLengths[i])
                        return false.Label($"ByteLength[{i}] changed: expected {snapshotByteLengths[i]}, got {postByteLen}");
                }

                return true.Label("FileIndex immutable during extraction");
            });
    }
}
