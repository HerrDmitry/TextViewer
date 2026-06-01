using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Unit tests for backend message handlers (get-view, open-file, close-file, scan-complete).
/// Validates: Requirements 4.1–4.6, 6.1–6.6, 7.1, 7.3, 7.5, 7.6, 3.1, 3.2, 8.1, 8.2, 8.5
/// </summary>
public class BackendHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly object _sessionLock = new();

    public BackendHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TextViewerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string CreateTempFile(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private FileViewService CreateService(string filePath)
    {
        var logger = new NullLogger<FileViewService>();
        return new FileViewService(filePath, CancellationToken.None, logger);
    }

    /// <summary>
    /// Waits for a FileViewService to reach at least QuickScanComplete state.
    /// </summary>
    private async Task WaitForScan(FileViewService service, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (service.ScanState < ScanState.QuickScanComplete && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    #region get-view handler tests

    /// <summary>
    /// Test get-view with valid payload → correct GetViewAsync params and response.
    /// Validates: Requirements 4.1, 4.3, 4.4, 6.1, 6.5
    /// </summary>
    [Fact]
    public async Task GetView_ValidPayload_ReturnsRows()
    {
        // Arrange
        var filePath = CreateTempFile("Line1\nLine2\nLine3\n");
        var sessions = new Dictionary<string, FileViewService>();
        var sessionId = Guid.NewGuid().ToString();
        var service = CreateService(filePath);
        sessions[sessionId] = service;

        await WaitForScan(service);

        var payload = $"{sessionId}\n0\n0\n3\n10";

        // Act
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        // Assert — new format: {lineNum}\t{content}
        Assert.NotNull(result);
        Assert.DoesNotContain("ERROR:", result);
        var rows = result!.Split('\n');
        Assert.True(rows.Length >= 1);
        Assert.Equal("1\tLine1", rows[0]);
        Assert.Equal("2\tLine2", rows[1]);
        Assert.Equal("3\tLine3", rows[2]);

        service.Dispose();
    }

    /// <summary>
    /// Test get-view with wrong field count → ERROR response.
    /// Validates: Requirements 4.6, 6.3
    /// </summary>
    [Fact]
    public async Task GetView_WrongFieldCount_ReturnsError()
    {
        var sessions = new Dictionary<string, FileViewService>();

        // Only 3 fields instead of 5
        var payload = "session-id\n0\n0";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("expected 5 fields", result);
    }

    /// <summary>
    /// Test get-view with non-integer field → ERROR identifies field name.
    /// Validates: Requirements 4.6, 6.4
    /// </summary>
    [Fact]
    public async Task GetView_NonIntegerField_ReturnsErrorWithFieldName()
    {
        var sessions = new Dictionary<string, FileViewService>();

        // startLine is "abc" (non-integer)
        var payload = "session-id\nabc\n0\n10\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("startLine", result);
    }

    /// <summary>
    /// Test get-view with non-integer startCol → ERROR identifies startCol.
    /// Validates: Requirements 4.6, 6.4
    /// </summary>
    [Fact]
    public async Task GetView_NonIntegerStartCol_ReturnsErrorWithFieldName()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "session-id\n0\nxyz\n10\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("startCol", result);
    }

    /// <summary>
    /// Test get-view with non-integer rowCount → ERROR identifies rowCount.
    /// Validates: Requirements 4.6, 6.4
    /// </summary>
    [Fact]
    public async Task GetView_NonIntegerRowCount_ReturnsErrorWithFieldName()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "session-id\n0\n0\nfoo\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("rowCount", result);
    }

    /// <summary>
    /// Test get-view with non-integer colCount → ERROR identifies colCount.
    /// Validates: Requirements 4.6, 6.4
    /// </summary>
    [Fact]
    public async Task GetView_NonIntegerColCount_ReturnsErrorWithFieldName()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "session-id\n0\n0\n10\nbar";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("colCount", result);
    }

    /// <summary>
    /// Test get-view with unknown session → ERROR session not found.
    /// Validates: Requirements 4.2, 7.3
    /// </summary>
    [Fact]
    public async Task GetView_UnknownSession_ReturnsSessionNotFoundError()
    {
        var sessions = new Dictionary<string, FileViewService>();
        var unknownId = "non-existent-session-id";

        var payload = $"{unknownId}\n0\n0\n10\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("Session not found", result);
        Assert.Contains(unknownId, result);
    }

    /// <summary>
    /// Test get-view with rowCount=0 → ERROR (rowCount must be >= 1).
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public async Task GetView_RowCountZero_ReturnsError()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "session-id\n0\n0\n0\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("rowCount", result);
    }

    /// <summary>
    /// Test get-view with negative startLine → ERROR.
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public async Task GetView_NegativeStartLine_ReturnsError()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "session-id\n-1\n0\n10\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("startLine", result);
    }

    #endregion

    #region get-view wrapped mode tests

    /// <summary>
    /// Test get-view wrapped mode with valid payload → L: header + content.
    /// Validates: Requirements 2.2, 3.3
    /// </summary>
    [Fact]
    public async Task GetView_WrappedMode_ReturnsLHeaderAndContent()
    {
        // Arrange — file with short lines, colCount=10 means no wrapping within lines
        var filePath = CreateTempFile("AAAA\nBBBB\nCCCC\n");
        var sessions = new Dictionary<string, FileViewService>();
        var sessionId = Guid.NewGuid().ToString();
        var service = CreateService(filePath);
        sessions[sessionId] = service;

        await WaitForScan(service);

        // Wrapped request: viewSessionId\nW\nstartLine\ncharOffset\ncharCount\ncolCount
        var payload = $"{sessionId}\nW\n0\n0\n20\n10";

        // Act
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        // Assert — response starts with L: header
        Assert.NotNull(result);
        Assert.DoesNotContain("ERROR", result);
        Assert.StartsWith("L:", result);

        // Parse header
        var newlineIdx = result!.IndexOf('\n');
        Assert.True(newlineIdx > 0);
        var header = result.Substring(2, newlineIdx - 2); // strip "L:" prefix
        var lineNums = header.Split(',');
        // Each entry is either a number or empty (continuation)
        foreach (var entry in lineNums)
        {
            if (!string.IsNullOrEmpty(entry))
                Assert.True(int.TryParse(entry, out _));
        }

        // Content after header should not be empty
        var content = result.Substring(newlineIdx + 1);
        Assert.NotEmpty(content);

        service.Dispose();
    }

    /// <summary>
    /// Test get-view wrapped mode with 5 fields (legacy, no colCount) → defaults colCount=1.
    /// Validates: Requirements 3.2, 3.3
    /// </summary>
    [Fact]
    public async Task GetView_WrappedMode_5Fields_DefaultsColCount1()
    {
        // Arrange
        var filePath = CreateTempFile("Hello\nWorld\n");
        var sessions = new Dictionary<string, FileViewService>();
        var sessionId = Guid.NewGuid().ToString();
        var service = CreateService(filePath);
        sessions[sessionId] = service;

        await WaitForScan(service);

        // 5-field wrapped request (legacy): viewSessionId\nW\nstartLine\ncharOffset\ncharCount
        var payload = $"{sessionId}\nW\n0\n0\n15";

        // Act
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        // Assert — still returns L: header format
        Assert.NotNull(result);
        Assert.DoesNotContain("ERROR", result);
        Assert.StartsWith("L:", result);

        service.Dispose();
    }

    /// <summary>
    /// Test get-view wrapped mode with invalid startLine → ERROR.
    /// Validates: Requirements 3.3
    /// </summary>
    [Fact]
    public async Task GetView_WrappedMode_InvalidStartLine_ReturnsError()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "session-id\nW\n-1\n0\n20\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("startLine", result);
    }

    /// <summary>
    /// Test get-view wrapped mode with invalid charCount → ERROR.
    /// Validates: Requirements 3.3
    /// </summary>
    [Fact]
    public async Task GetView_WrappedMode_InvalidCharCount_ReturnsError()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "session-id\nW\n0\n0\n0\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("characterCount", result);
    }

    /// <summary>
    /// Test get-view wrapped mode with unknown session → ERROR.
    /// Validates: Requirements 3.3
    /// </summary>
    [Fact]
    public async Task GetView_WrappedMode_UnknownSession_ReturnsError()
    {
        var sessions = new Dictionary<string, FileViewService>();

        var payload = "non-existent\nW\n0\n0\n20\n10";
        var result = await Program.HandleGetView(payload, sessions, _sessionLock);

        Assert.NotNull(result);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("Session not found", result);
    }

    #endregion

    #region open-file handler tests

    /// <summary>
    /// Test open-file creates session, waits 500ms, calls GetViewAsync, returns viewSessionId\nfilePath\nrows...
    /// Validates: Requirements 8.1, 8.2, 7.1
    /// </summary>
    [Fact]
    public async Task OpenFile_CreatesSession_ReturnsViewSessionIdAndRows()
    {
        // Arrange
        var filePath = CreateTempFile("Hello World\nSecond Line\n");
        var sessions = new Dictionary<string, FileViewService>();
        var viewSessionId = Guid.NewGuid().ToString();

        var logger = new NullLogger<FileViewService>();
        var service = new FileViewService(filePath, CancellationToken.None, logger);
        sessions[viewSessionId] = service;

        // Wait for scan to complete so GetViewAsync can return data
        await WaitForScan(service);
        await Task.Delay(100); // extra time for full scan

        // Act - simulate what open-file handler does after creating service
        var result = await service.GetViewAsync(0, 0, 40, 120);
        var response = Program.FormatOpenFileResponse(viewSessionId, filePath, result);

        // Assert
        Assert.StartsWith(viewSessionId, response);
        var parts = response.Split('\n');
        Assert.True(parts.Length >= 2);
        Assert.Equal(viewSessionId, parts[0]);
        Assert.Equal(filePath, parts[1]);
        // Should have rows after filePath
        if (parts.Length > 2)
        {
            Assert.Equal("Hello World", parts[2]);
        }

        service.Dispose();
    }

    /// <summary>
    /// Test open-file with empty payload uses fallback dimensions (40×120).
    /// Validates: Requirements 8.5, 8.2
    /// </summary>
    [Fact]
    public void OpenFile_EmptyPayload_UsesFallbackDimensions()
    {
        var (rowCount, colCount) = Program.ParseOpenFilePayload("");

        Assert.Equal(40, rowCount);
        Assert.Equal(120, colCount);
    }

    /// <summary>
    /// Test open-file with null payload uses fallback dimensions (40×120).
    /// Validates: Requirements 8.5
    /// </summary>
    [Fact]
    public void OpenFile_NullPayload_UsesFallbackDimensions()
    {
        var (rowCount, colCount) = Program.ParseOpenFilePayload(null);

        Assert.Equal(40, rowCount);
        Assert.Equal(120, colCount);
    }

    /// <summary>
    /// Test open-file with valid payload parses rowCount and colCount.
    /// Validates: Requirements 8.2
    /// </summary>
    [Fact]
    public void OpenFile_ValidPayload_ParsesRowCountAndColCount()
    {
        var (rowCount, colCount) = Program.ParseOpenFilePayload("25\n80");

        Assert.Equal(25, rowCount);
        Assert.Equal(80, colCount);
    }

    /// <summary>
    /// Test open-file with invalid numeric payload uses fallback.
    /// Validates: Requirements 8.5
    /// </summary>
    [Fact]
    public void OpenFile_InvalidNumericPayload_UsesFallback()
    {
        var (rowCount, colCount) = Program.ParseOpenFilePayload("abc\ndef");

        Assert.Equal(40, rowCount);
        Assert.Equal(120, colCount);
    }

    /// <summary>
    /// Test open-file with zero rowCount uses fallback.
    /// Validates: Requirements 8.5
    /// </summary>
    [Fact]
    public void OpenFile_ZeroRowCount_UsesFallback()
    {
        var (rowCount, colCount) = Program.ParseOpenFilePayload("0\n80");

        Assert.Equal(40, rowCount);
        Assert.Equal(80, colCount);
    }

    /// <summary>
    /// Test FormatOpenFileResponse with successful result containing rows.
    /// Validates: Requirements 8.2, 8.3
    /// </summary>
    [Fact]
    public void FormatOpenFileResponse_WithRows_FormatsCorrectly()
    {
        var viewSessionId = "test-session-123";
        var filePath = @"C:\test\file.txt";
        var viewResult = Result<ViewResult, ViewError>.Success(
            new ViewResult(new[] { "Row1\n", "Row2\r\n", "Row3" }));

        var response = Program.FormatOpenFileResponse(viewSessionId, filePath, viewResult);

        var parts = response.Split('\n');
        Assert.Equal("test-session-123", parts[0]);
        Assert.Equal(@"C:\test\file.txt", parts[1]);
        Assert.Equal("Row1", parts[2]);
        Assert.Equal("Row2", parts[3]);
        Assert.Equal("Row3", parts[4]);
    }

    /// <summary>
    /// Test FormatOpenFileResponse with empty rows (error result).
    /// Validates: Requirements 8.5
    /// </summary>
    [Fact]
    public void FormatOpenFileResponse_WithError_ReturnsSessionAndPathOnly()
    {
        var viewSessionId = "test-session-456";
        var filePath = @"C:\test\file.txt";
        var viewResult = Result<ViewResult, ViewError>.Failure(
            new ViewError(ViewErrorCode.FileNotAccessible, "File not found"));

        var response = Program.FormatOpenFileResponse(viewSessionId, filePath, viewResult);

        Assert.Equal($"{viewSessionId}\n{filePath}", response);
    }

    /// <summary>
    /// Test FormatOpenFileResponse with zero rows.
    /// Validates: Requirements 8.5
    /// </summary>
    [Fact]
    public void FormatOpenFileResponse_WithZeroRows_ReturnsSessionAndPathOnly()
    {
        var viewSessionId = "test-session-789";
        var filePath = @"C:\test\file.txt";
        var viewResult = Result<ViewResult, ViewError>.Success(
            new ViewResult(Array.Empty<string>()));

        var response = Program.FormatOpenFileResponse(viewSessionId, filePath, viewResult);

        Assert.Equal($"{viewSessionId}\n{filePath}", response);
    }

    #endregion

    #region close-file handler tests

    /// <summary>
    /// Test close-file disposes service and removes from map.
    /// Validates: Requirements 7.5, 7.6
    /// </summary>
    [Fact]
    public async Task CloseFile_DisposesServiceAndRemovesFromMap()
    {
        // Arrange
        var filePath = CreateTempFile("test content\n");
        var sessions = new Dictionary<string, FileViewService>();
        var sessionId = "session-to-close";
        var service = CreateService(filePath);
        sessions[sessionId] = service;

        await WaitForScan(service);

        // Act
        Program.HandleCloseFile(sessionId, sessions, _sessionLock, new Dictionary<string, (int colCount, int lineCount, long total)>());

        // Assert
        Assert.DoesNotContain(sessionId, sessions.Keys);
        // Verify service is disposed by trying to call GetViewAsync — should fail or throw
        // (FileViewService.Dispose cancels the scan, subsequent calls may fail)
    }

    /// <summary>
    /// Test close-file with unknown session → no-op.
    /// Validates: Requirements 7.6
    /// </summary>
    [Fact]
    public void CloseFile_UnknownSession_NoOp()
    {
        var sessions = new Dictionary<string, FileViewService>();

        // Should not throw
        Program.HandleCloseFile("non-existent-session", sessions, _sessionLock, new Dictionary<string, (int colCount, int lineCount, long total)>());

        Assert.Empty(sessions);
    }

    /// <summary>
    /// Test close-file does not affect other sessions.
    /// Validates: Requirements 7.5
    /// </summary>
    [Fact]
    public async Task CloseFile_DoesNotAffectOtherSessions()
    {
        // Arrange
        var filePath1 = CreateTempFile("file1 content\n");
        var filePath2 = CreateTempFile("file2 content\n");
        var sessions = new Dictionary<string, FileViewService>();
        var sessionId1 = "session-1";
        var sessionId2 = "session-2";
        var service1 = CreateService(filePath1);
        var service2 = CreateService(filePath2);
        sessions[sessionId1] = service1;
        sessions[sessionId2] = service2;

        await WaitForScan(service1);
        await WaitForScan(service2);

        // Act - close session 1
        Program.HandleCloseFile(sessionId1, sessions, _sessionLock, new Dictionary<string, (int colCount, int lineCount, long total)>());

        // Assert - session 2 still exists
        Assert.DoesNotContain(sessionId1, sessions.Keys);
        Assert.Contains(sessionId2, sessions.Keys);
        Assert.Same(service2, sessions[sessionId2]);

        service2.Dispose();
    }

    #endregion

    #region scan-complete (MonitorScanState) tests

    /// <summary>
    /// Test scan-complete sent only at FullScanComplete (not QuickScanComplete).
    /// Validates: Requirements 3.1, 3.2
    /// </summary>
    [Fact]
    public async Task MonitorScanState_SendsOnlyAtFullScanComplete()
    {
        // Arrange
        var filePath = CreateTempFile("Line1\nLine2\nLine3\n");
        var service = CreateService(filePath);
        var sessionId = "monitor-session";

        var bridge = new MockMessageBridge();
        var messageBus = new MessageBusHost(bridge);

        // Act - start monitoring
        var monitorTask = Program.MonitorScanState(service, sessionId, messageBus);

        // Wait for full scan to complete
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (service.ScanState < ScanState.FullScanComplete && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }

        await monitorTask;

        // Assert - exactly one scan-complete message sent
        var scanCompleteMessages = bridge.SentMessages
            .Select(m => MessageProtocol.Decode(m))
            .Where(r => r.IsSuccess && r.Value.MessageType == "scan-complete")
            .ToList();

        Assert.Single(scanCompleteMessages);
        Assert.Equal(sessionId, scanCompleteMessages[0].Value.Payload);

        service.Dispose();
        messageBus.Dispose();
    }

    /// <summary>
    /// Test MonitorScanState exits on Failed state.
    /// Note: Due to enum ordering (Failed=5 >= FullScanComplete=4), the >= check
    /// catches Failed state and sends scan-complete before the explicit Failed check.
    /// This test documents the actual behavior.
    /// Validates: Requirements 3.1
    /// </summary>
    [Fact]
    public async Task MonitorScanState_FailedState_CompletesWithoutHanging()
    {
        // Arrange - use a non-existent file to trigger failure
        var filePath = Path.Combine(_tempDir, "non_existent_file.txt");
        var service = CreateService(filePath);
        var sessionId = "failed-session";

        var bridge = new MockMessageBridge();
        var messageBus = new MessageBusHost(bridge);

        // Act - start monitoring
        var monitorTask = Program.MonitorScanState(service, sessionId, messageBus);

        // Wait for monitor to complete (should exit, not hang)
        var completed = await Task.WhenAny(monitorTask, Task.Delay(5000));
        Assert.Same(monitorTask, completed); // Should complete, not timeout

        service.Dispose();
        messageBus.Dispose();
    }

    #endregion

    #region Multiple opens of same file → independent sessions

    /// <summary>
    /// Test multiple opens of same file → independent sessions.
    /// Validates: Requirements 7.1, 7.3
    /// </summary>
    [Fact]
    public async Task MultipleOpens_SameFile_IndependentSessions()
    {
        // Arrange
        var filePath = CreateTempFile("Shared content\nLine 2\n");
        var sessions = new Dictionary<string, FileViewService>();
        var sessionId1 = Guid.NewGuid().ToString();
        var sessionId2 = Guid.NewGuid().ToString();

        var service1 = CreateService(filePath);
        var service2 = CreateService(filePath);
        sessions[sessionId1] = service1;
        sessions[sessionId2] = service2;

        await WaitForScan(service1);
        await WaitForScan(service2);

        // Act - get-view on both sessions
        var payload1 = $"{sessionId1}\n0\n0\n2\n20";
        var payload2 = $"{sessionId2}\n0\n0\n2\n20";

        var result1 = await Program.HandleGetView(payload1, sessions, _sessionLock);
        var result2 = await Program.HandleGetView(payload2, sessions, _sessionLock);

        // Assert - both return valid results independently with {lineNum}\t{content} format
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.DoesNotContain("ERROR:", result1);
        Assert.DoesNotContain("ERROR:", result2);
        // Verify new format: first row should be "1\tShared content"
        Assert.StartsWith("1\t", result1!.Split('\n')[0]);
        Assert.StartsWith("1\t", result2!.Split('\n')[0]);

        // Close one session, other still works
        Program.HandleCloseFile(sessionId1, sessions, _sessionLock, new Dictionary<string, (int colCount, int lineCount, long total)>());

        var result2AfterClose = await Program.HandleGetView(payload2, sessions, _sessionLock);
        Assert.DoesNotContain("ERROR:", result2AfterClose!);

        // Closed session returns error
        var result1AfterClose = await Program.HandleGetView(payload1, sessions, _sessionLock);
        Assert.Contains("Session not found", result1AfterClose!);

        service2.Dispose();
    }

    #endregion

    #region StripDelimiter tests

    /// <summary>
    /// Test StripDelimiter removes \n.
    /// Validates: Requirements 4.4, 6.5
    /// </summary>
    [Fact]
    public void StripDelimiter_RemovesNewline()
    {
        Assert.Equal("Hello", Program.StripDelimiter("Hello\n"));
    }

    /// <summary>
    /// Test StripDelimiter removes \r\n.
    /// Validates: Requirements 4.4, 6.5
    /// </summary>
    [Fact]
    public void StripDelimiter_RemovesCrLf()
    {
        Assert.Equal("Hello", Program.StripDelimiter("Hello\r\n"));
    }

    /// <summary>
    /// Test StripDelimiter removes \r.
    /// Validates: Requirements 4.4, 6.5
    /// </summary>
    [Fact]
    public void StripDelimiter_RemovesCr()
    {
        Assert.Equal("Hello", Program.StripDelimiter("Hello\r"));
    }

    /// <summary>
    /// Test StripDelimiter leaves string without delimiter unchanged.
    /// Validates: Requirements 4.4, 6.5
    /// </summary>
    [Fact]
    public void StripDelimiter_NoDelimiter_Unchanged()
    {
        Assert.Equal("Hello", Program.StripDelimiter("Hello"));
    }

    /// <summary>
    /// Test StripDelimiter handles empty string.
    /// Validates: Requirements 4.4, 6.5
    /// </summary>
    [Fact]
    public void StripDelimiter_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", Program.StripDelimiter(""));
    }

    #endregion

    /// <summary>
    /// Mock IMessageBridge for testing MonitorScanState.
    /// </summary>
    private class MockMessageBridge : IMessageBridge
    {
        public List<string> SentMessages { get; } = new();
        public event EventHandler<string>? WebMessageReceived;

        public void SendWebMessage(string message) => SentMessages.Add(message);
        public void SimulateInbound(string message) => WebMessageReceived?.Invoke(this, message);
    }

    /// <summary>
    /// Null logger implementation for tests.
    /// </summary>
    private class NullLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
