using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Unit tests for FileIndex file opening and Quick_Scan phase.
/// Validates: Requirements 1.1, 1.2, 1.3, 1.4, 2.2, 2.3, 2.4, 2.5, 6.6, 7.1, 7.2, 7.4, 7.5
/// </summary>
public class FileIndexOpeningAndQuickScanTests
{
    private readonly ILogger<FileIndex> _logger = NullLogger<FileIndex>.Instance;

    // --- Requirement 1.1: FileIndex opens with FileShare.ReadWrite ---

    [Fact]
    public async Task FileIndex_OpensWithFileShareReadWrite_OtherProcessCanWrite()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "hello\nworld\n");
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            // After FileIndex opens the file, another process should be able to write
            // Since StartScanAsync completes, we verify by opening with write access
            // The file was opened with FileShare.ReadWrite, so this should succeed
            using var writeStream = new FileStream(
                tempFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            writeStream.WriteByte(0x41); // Write a byte — should not throw
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 1.2: FileIndex opens with FileAccess.Read ---

    [Fact]
    public async Task FileIndex_OpensWithFileAccessRead_FileNotModified()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "line1\nline2\nline3";
            File.WriteAllText(tempFile, content);
            var beforeBytes = File.ReadAllBytes(tempFile);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);
            await fileIndex.StartScanAsync();

            var afterBytes = File.ReadAllBytes(tempFile);
            Assert.Equal(beforeBytes, afterBytes);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 1.4: Missing file → Failed state + correct Error format ---

    [Fact]
    public async Task MissingFile_FailedState_CorrectErrorFormat()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".txt");
        using var cts = new CancellationTokenSource();
        using var fileIndex = new FileIndex(nonExistentPath, cts.Token, _logger);

        await fileIndex.StartScanAsync();

        Assert.Equal(ScanState.Failed, fileIndex.State);
        Assert.Equal($"Failed to open {nonExistentPath}: FileNotFoundException", fileIndex.Error);
    }

    // --- Requirement 1.3: Access denied → Failed state + correct Error format ---

    [Fact]
    public async Task DirectoryPath_FailedState_CorrectErrorFormat()
    {
        // Using a directory path triggers an IOException (or UnauthorizedAccessException)
        // when trying to open it as a file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempDir, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(ScanState.Failed, fileIndex.State);
            // Opening a directory as a file throws UnauthorizedAccessException on Windows
            Assert.Contains("Failed to open", fileIndex.Error);
            Assert.Contains(tempDir, fileIndex.Error);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    // --- Requirement 1.3: IOException on open → Failed state + correct Error format ---

    [Fact]
    public async Task IOException_OnOpen_FailedState_CorrectErrorFormat()
    {
        // Trigger IOException by exclusively locking a file, then trying to open it
        var tempFile = Path.GetTempFileName();
        try
        {
            // Open with exclusive lock (FileShare.None) to prevent other opens
            using var exclusiveLock = new FileStream(
                tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(ScanState.Failed, fileIndex.State);
            Assert.Equal($"Failed to open {tempFile}: IOException", fileIndex.Error);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.2: Quick_Scan identifies LF line endings ---

    [Fact]
    public async Task QuickScan_IdentifiesLF_LineEndings()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "abc\ndef\n" — 2 lines terminated by LF
            File.WriteAllBytes(tempFile, "abc\ndef\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(2, fileIndex.Index.LineCount);
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(0)); // "abc" + LF = 4
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(1)); // "def" + LF = 4
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.2: Quick_Scan identifies CR line endings ---

    [Fact]
    public async Task QuickScan_IdentifiesCR_LineEndings()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "abc\rdef\r" — 2 lines terminated by CR
            File.WriteAllBytes(tempFile, "abc\rdef\r"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(2, fileIndex.Index.LineCount);
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(0)); // "abc" + CR = 4
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(1)); // "def" + CR = 4
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.2: Quick_Scan identifies CRLF line endings ---

    [Fact]
    public async Task QuickScan_IdentifiesCRLF_LineEndings()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "abc\r\ndef\r\n" — 2 lines terminated by CRLF
            File.WriteAllBytes(tempFile, "abc\r\ndef\r\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(2, fileIndex.Index.LineCount);
            Assert.Equal(5UL, fileIndex.Index.GetByteLength(0)); // "abc" + CRLF = 5
            Assert.Equal(5UL, fileIndex.Index.GetByteLength(1)); // "def" + CRLF = 5
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.2: Quick_Scan handles mixed line endings ---

    [Fact]
    public async Task QuickScan_HandlesMixedLineEndings()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "abc\ndef\r\nghi\rjkl" — mixed: LF, CRLF, CR, unterminated
            File.WriteAllBytes(tempFile, "abc\ndef\r\nghi\rjkl"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(4, fileIndex.Index.LineCount);
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(0)); // "abc" + LF = 4
            Assert.Equal(5UL, fileIndex.Index.GetByteLength(1)); // "def" + CRLF = 5
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(2)); // "ghi" + CR = 4
            Assert.Equal(3UL, fileIndex.Index.GetByteLength(3)); // "jkl" (unterminated) = 3
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.3: Quick_Scan Byte_Length includes delimiter bytes ---

    [Fact]
    public async Task QuickScan_ByteLength_IncludesDelimiterBytes()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // LF delimiter = 1 byte, CR = 1 byte, CRLF = 2 bytes
            File.WriteAllBytes(tempFile, "a\nb\rc\r\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(3, fileIndex.Index.LineCount);
            Assert.Equal(2UL, fileIndex.Index.GetByteLength(0)); // "a" + LF = 2
            Assert.Equal(2UL, fileIndex.Index.GetByteLength(1)); // "b" + CR = 2
            Assert.Equal(3UL, fileIndex.Index.GetByteLength(2)); // "c" + CRLF = 3
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.3: Quick_Scan final unterminated line stores content bytes only ---

    [Fact]
    public async Task QuickScan_FinalUnterminatedLine_StoresContentBytesOnly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "hello\nworld" — "world" has no trailing delimiter
            File.WriteAllBytes(tempFile, "hello\nworld"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(2, fileIndex.Index.LineCount);
            Assert.Equal(6UL, fileIndex.Index.GetByteLength(0)); // "hello" + LF = 6
            Assert.Equal(5UL, fileIndex.Index.GetByteLength(1)); // "world" = 5 (no delimiter)
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.4: Empty file → 0 lines ---

    [Fact]
    public async Task EmptyFile_ZeroLines()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Empty file — zero bytes
            File.WriteAllBytes(tempFile, Array.Empty<byte>());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(0, fileIndex.Index.LineCount);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 2.5: Quick_Scan error → LineIndex empty, no partial data ---

    [Fact]
    public async Task QuickScan_Error_LineIndexEmpty_NoPartialData()
    {
        // We test this by using a file that doesn't exist (triggers error before scan)
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".txt");
        using var cts = new CancellationTokenSource();
        using var fileIndex = new FileIndex(nonExistentPath, cts.Token, _logger);

        await fileIndex.StartScanAsync();

        Assert.Equal(ScanState.Failed, fileIndex.State);
        Assert.Equal(0, fileIndex.Index.LineCount);
    }

    // --- Requirement 7.5: CancellationToken → state = Cancelled ---

    [Fact]
    public async Task CancellationToken_PreCancelled_StateCancelled()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write enough data that the scan would take time
            File.WriteAllBytes(tempFile, "line1\nline2\nline3\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancel the token

            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);
            await fileIndex.StartScanAsync();

            Assert.Equal(ScanState.Cancelled, fileIndex.State);
            Assert.Equal(0, fileIndex.Index.LineCount); // LineIndex cleared on cancellation
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 7.1, 7.2: ScanState transitions in correct order (happy path) ---

    [Fact]
    public async Task ScanState_TransitionsInCorrectOrder_HappyPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "hello\nworld\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            // Before scan
            Assert.Equal(ScanState.NotStarted, fileIndex.State);

            await fileIndex.StartScanAsync();

            // After both Quick_Scan and Full_Scan complete
            Assert.Equal(ScanState.FullScanComplete, fileIndex.State);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 7.4: Error property format matches spec ---

    [Fact]
    public async Task ErrorProperty_FormatMatchesSpec_FileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".txt");
        using var cts = new CancellationTokenSource();
        using var fileIndex = new FileIndex(path, cts.Token, _logger);

        await fileIndex.StartScanAsync();

        Assert.Equal($"Failed to open {path}: FileNotFoundException", fileIndex.Error);
    }

    [Fact]
    public async Task ErrorProperty_NullWhenNoError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "test\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Null(fileIndex.Error);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 6.6: Log levels ---

    [Fact]
    public async Task LogLevel_ScanStart_Information()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "test\n"u8.ToArray());
            var testLogger = new TestLogger();
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, testLogger);

            await fileIndex.StartScanAsync();

            // Verify scan start is logged at Information level
            Assert.Contains(testLogger.LogEntries,
                e => e.Level == LogLevel.Information && e.Message.Contains("Starting scan"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LogLevel_AccessError_Error()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".txt");
        var testLogger = new TestLogger();
        using var cts = new CancellationTokenSource();
        using var fileIndex = new FileIndex(nonExistentPath, cts.Token, testLogger);

        await fileIndex.StartScanAsync();

        // Verify access error is logged at Error level
        Assert.Contains(testLogger.LogEntries,
            e => e.Level == LogLevel.Error && e.Message.Contains("FileNotFoundException"));
    }

    /// <summary>
    /// Simple test logger that captures log entries for assertion.
    /// </summary>
    private class TestLogger : ILogger<FileIndex>
    {
        public List<LogEntry> LogEntries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LogEntries.Add(new LogEntry
            {
                Level = logLevel,
                Message = formatter(state, exception),
                Exception = exception
            });
        }
    }

    private class LogEntry
    {
        public LogLevel Level { get; init; }
        public string Message { get; init; } = "";
        public Exception? Exception { get; init; }
    }
}
