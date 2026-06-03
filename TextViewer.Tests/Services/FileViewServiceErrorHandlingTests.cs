using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Unit tests for FileViewService error handling.
/// Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5
/// </summary>
public class FileViewServiceErrorHandlingTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFile(string content = "Hello\nWorld\n")
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_err_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Test: FileNotFoundException → FileNotAccessible result.
    /// Create a FileViewService with a non-existent file path.
    /// Wait briefly for scan to start/fail. Call GetViewAsync with valid params.
    /// Assert result is Failure with ViewErrorCode.FileNotAccessible and message contains the file path.
    /// Validates: Requirement 7.3
    /// </summary>
    [Fact]
    public async Task GetViewAsync_FileNotFound_ReturnsFileNotAccessible()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.txt");
        var logger = NullLogger<FileViewService>.Instance;
        using var service = new FileViewService(nonExistentPath, CancellationToken.None, logger);

        // Wait briefly for scan to start and fail
        await Task.Delay(200);

        // Act
        var result = await service.GetViewAsync(0, 0, 1, 80);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ViewErrorCode.FileNotAccessible, result.Error.Code);
        Assert.Contains(nonExistentPath, result.Error.Message);
    }

    /// <summary>
    /// Test: Cancellation → OperationCanceledException.
    /// Create a FileViewService with a real temp file.
    /// Create an already-cancelled CancellationToken.
    /// Call GetViewAsync passing the cancelled token.
    /// Assert OperationCanceledException is thrown.
    /// Validates: Requirement 7.4
    /// </summary>
    [Fact]
    public async Task GetViewAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var path = CreateTempFile();
        var logger = NullLogger<FileViewService>.Instance;
        using var service = new FileViewService(path, CancellationToken.None, logger);

        // Wait for scan to complete
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.ScanState < ScanState.ScanComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        // Create an already-cancelled token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetViewAsync(0, 0, 1, 80, cts.Token));
    }

    /// <summary>
    /// Test: ViewError has correct code + message.
    /// Create a ViewError with ViewErrorCode.IoError and a message.
    /// Assert Code and Message properties match.
    /// Validates: Requirement 7.5
    /// </summary>
    [Fact]
    public void ViewError_HasCorrectCodeAndMessage()
    {
        // Arrange
        var code = ViewErrorCode.IoError;
        var message = "Read error: /some/path: IOException";

        // Act
        var error = new ViewError(code, message);

        // Assert
        Assert.Equal(ViewErrorCode.IoError, error.Code);
        Assert.Equal("Read error: /some/path: IOException", error.Message);
    }

    /// <summary>
    /// Test: Service-level cancellation → OperationCanceledException.
    /// Create a FileViewService with an already-cancelled service-level CancellationToken.
    /// Call GetViewAsync. Assert OperationCanceledException is thrown.
    /// Validates: Requirement 7.4
    /// </summary>
    [Fact]
    public async Task GetViewAsync_ServiceLevelCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var path = CreateTempFile();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var logger = NullLogger<FileViewService>.Instance;
        using var service = new FileViewService(path, cts.Token, logger);

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetViewAsync(0, 0, 1, 80));
    }
}
