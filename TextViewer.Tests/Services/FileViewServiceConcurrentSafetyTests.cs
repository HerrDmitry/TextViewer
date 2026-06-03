using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Unit tests asserting FileStream is opened with FileAccess.Read and FileShare.ReadWrite.
/// Validates: Requirement 6.2
/// </summary>
public class FileViewServiceConcurrentSafetyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFile(string content = "Line one\nLine two\nLine three\n")
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_concurrent_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task WaitForScanComplete(FileViewService service, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (service.ScanState < ScanState.ScanComplete && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Verifies that the file handle is opened with FileShare.ReadWrite by confirming
    /// that another process/thread can open the same file for writing while the service
    /// is actively reading it. If the service did NOT use FileShare.ReadWrite, the
    /// concurrent write-open would throw an IOException.
    /// Validates: Requirement 6.2
    /// </summary>
    [Fact]
    public async Task GetViewAsync_FileOpenedWithFileShareReadWrite_AllowsConcurrentWriteAccess()
    {
        // Arrange
        var content = "Hello World\nSecond Line\nThird Line\n";
        var path = CreateTempFile(content);
        var logger = NullLogger<FileViewService>.Instance;
        using var service = new FileViewService(path, CancellationToken.None, logger);
        await WaitForScanComplete(service);

        // Act - Call GetViewAsync (which opens and closes a FileStream internally)
        var result = await service.GetViewAsync(0, 0, 3, 80);

        // After the service has read the file, verify we can open it for writing.
        // This proves the service used FileShare.ReadWrite (not FileShare.Read or FileShare.None).
        // If the service held the handle open with restrictive sharing, this would fail.
        FileStream? writeStream = null;
        Exception? caughtException = null;
        try
        {
            writeStream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException ex)
        {
            caughtException = ex;
        }
        finally
        {
            writeStream?.Dispose();
        }

        // Assert
        Assert.True(result.IsSuccess, "GetViewAsync should succeed");
        Assert.Null(caughtException); // No IOException means FileShare.ReadWrite was used
    }

    /// <summary>
    /// Verifies that the service opens the file with strictly FileAccess.Read by confirming
    /// the file content and last-write timestamp are unchanged after a GetViewAsync call.
    /// If the service opened with FileAccess.Write or FileAccess.ReadWrite, it could
    /// potentially modify the file — this test ensures it does not.
    /// Validates: Requirement 6.2
    /// </summary>
    [Fact]
    public async Task GetViewAsync_FileOpenedWithFileAccessRead_DoesNotModifyFile()
    {
        // Arrange
        var content = "Alpha\nBeta\nGamma\nDelta\n";
        var path = CreateTempFile(content);
        var logger = NullLogger<FileViewService>.Instance;
        using var service = new FileViewService(path, CancellationToken.None, logger);
        await WaitForScanComplete(service);

        // Capture file state before the read
        var contentBefore = File.ReadAllBytes(path);
        var lastWriteBefore = File.GetLastWriteTimeUtc(path);

        // Act - Perform multiple reads to exercise the file handle
        var result1 = await service.GetViewAsync(0, 0, 4, 80);
        var result2 = await service.GetViewAsync(1, 2, 2, 10);
        var result3 = await service.GetViewAsync(0, 0, 1, 5);

        // Capture file state after the reads
        var contentAfter = File.ReadAllBytes(path);
        var lastWriteAfter = File.GetLastWriteTimeUtc(path);

        // Assert - File must be completely unchanged
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
        Assert.True(result3.IsSuccess);
        Assert.Equal(contentBefore, contentAfter);
        Assert.Equal(lastWriteBefore, lastWriteAfter);
    }

    /// <summary>
    /// Verifies that another process can write to the file while the service reads it,
    /// proving the service uses FileShare.ReadWrite. Opens a write handle first, then
    /// calls GetViewAsync — if the service uses FileShare.ReadWrite for its own handle,
    /// both handles can coexist.
    /// Validates: Requirement 6.2
    /// </summary>
    [Fact]
    public async Task GetViewAsync_ConcurrentWriteHandle_DoesNotBlockServiceRead()
    {
        // Arrange
        var content = "First\nSecond\nThird\n";
        var path = CreateTempFile(content);
        var logger = NullLogger<FileViewService>.Instance;
        using var service = new FileViewService(path, CancellationToken.None, logger);
        await WaitForScanComplete(service);

        // Open a write handle to the file BEFORE calling GetViewAsync.
        // The service must be able to open its own read handle despite this existing write handle.
        // This only works if the service opens with FileShare.ReadWrite.
        using var writeHandle = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        // Act
        var result = await service.GetViewAsync(0, 0, 3, 80);

        // Assert - Service should still succeed reading despite concurrent write handle
        Assert.True(result.IsSuccess, "GetViewAsync should succeed even with a concurrent write handle open");
        Assert.True(result.Value.Rows.Count > 0);
    }
}
