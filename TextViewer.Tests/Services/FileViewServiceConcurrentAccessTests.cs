using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Integration test for concurrent access to FileViewService.
/// Validates: Requirement 6.1 — at least 4 concurrent requests produce correct, independent results.
/// </summary>
public class FileViewServiceConcurrentAccessTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFileWith20Lines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_concurrent_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);

        // 20 lines: "Line00\n" through "Line19\n"
        var lines = Enumerable.Range(0, 20).Select(i => $"Line{i:D2}");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    /// <summary>
    /// Issue 4 concurrent GetViewAsync requests with different parameters against the same FileViewService.
    /// Assert all produce correct, independent results matching the known file content.
    /// Validates: Requirement 6.1
    /// </summary>
    [Fact]
    public async Task GetViewAsync_FourConcurrentRequests_AllProduceCorrectIndependentResults()
    {
        // Arrange
        var path = CreateTempFileWith20Lines();
        var logger = NullLogger<FileViewService>.Instance;
        using var service = new FileViewService(path, CancellationToken.None, logger);

        // Wait for scan to complete
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.ScanState < ScanState.QuickScanComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(service.ScanState >= ScanState.QuickScanComplete,
            "Scan did not complete in time");

        // Act — issue 4 concurrent requests with different startLine values
        var task1 = service.GetViewAsync(startLine: 0, startCol: 0, rowCount: 5, colCount: 80);
        var task2 = service.GetViewAsync(startLine: 5, startCol: 0, rowCount: 5, colCount: 80);
        var task3 = service.GetViewAsync(startLine: 10, startCol: 0, rowCount: 5, colCount: 80);
        var task4 = service.GetViewAsync(startLine: 15, startCol: 0, rowCount: 5, colCount: 80);

        var results = await Task.WhenAll(task1, task2, task3, task4);

        // Assert — all results are successful
        for (int i = 0; i < 4; i++)
        {
            Assert.True(results[i].IsSuccess, $"Request {i + 1} should succeed");
        }

        // Assert — each result contains the correct 5 rows for its startLine
        var result1Rows = results[0].Value.Rows;
        var result2Rows = results[1].Value.Rows;
        var result3Rows = results[2].Value.Rows;
        var result4Rows = results[3].Value.Rows;

        Assert.Equal(5, result1Rows.Count);
        Assert.Equal(5, result2Rows.Count);
        Assert.Equal(5, result3Rows.Count);
        Assert.Equal(5, result4Rows.Count);

        // Verify content for request 1: lines 0-4
        for (int i = 0; i < 5; i++)
        {
            Assert.StartsWith($"Line{i:D2}", result1Rows[i]);
        }

        // Verify content for request 2: lines 5-9
        for (int i = 0; i < 5; i++)
        {
            Assert.StartsWith($"Line{(i + 5):D2}", result2Rows[i]);
        }

        // Verify content for request 3: lines 10-14
        for (int i = 0; i < 5; i++)
        {
            Assert.StartsWith($"Line{(i + 10):D2}", result3Rows[i]);
        }

        // Verify content for request 4: lines 15-19
        for (int i = 0; i < 5; i++)
        {
            Assert.StartsWith($"Line{(i + 15):D2}", result4Rows[i]);
        }

        // Assert results are independent — each has different content
        Assert.NotEqual(result1Rows[0], result2Rows[0]);
        Assert.NotEqual(result2Rows[0], result3Rows[0]);
        Assert.NotEqual(result3Rows[0], result4Rows[0]);
    }
}
