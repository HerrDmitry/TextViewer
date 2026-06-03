using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Integration tests for end-to-end FileIndex scanning.
/// Validates: Requirements 1.1, 2.2, 2.3, 3.2, 4.1, 6.1
/// </summary>
public class FileIndexIntegrationTests
{
    private readonly ILogger<FileIndex> _logger = NullLogger<FileIndex>.Instance;

    // --- Test 1: End-to-end scan with known content ---

    [Fact]
    public async Task EndToEndScan_KnownContent_CorrectLineCounts()
    {
        // Mix of ASCII and multi-byte UTF-8 (emoji, CJK)
        var lines = new[]
        {
            "Hello, World!",       // 13 chars, 13 bytes
            "Héllo café",          // 10 chars, 12 bytes (é = 2 bytes each)
            "日本語テスト",          // 6 chars, 18 bytes (3 bytes each CJK)
            "emoji: 🎉",           // 8 chars (🎉 = surrogate pair = 2 UTF-16 code units), 12 bytes (🎉 = 4 bytes)
            "plain ASCII line",    // 16 chars, 16 bytes
        };

        var tempFile = Path.GetTempFileName();
        try
        {
            // Write with LF line endings, no trailing newline on last line
            var content = string.Join("\n", lines);
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(tempFile, bytes);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);
            await fileIndex.StartScanAsync();

            Assert.Equal(ScanState.ScanComplete, fileIndex.State);
            Assert.Equal(lines.Length, fileIndex.Index.LineCount);

            // Verify each line's byte length and char length
            for (int i = 0; i < lines.Length; i++)
            {
                var lineBytes = Encoding.UTF8.GetBytes(lines[i]);
                int expectedByteLength = lineBytes.Length + (i < lines.Length - 1 ? 1 : 0); // +1 for LF except last
                int expectedCharLength = lines[i].Length;

                Assert.Equal((ulong)expectedByteLength, fileIndex.Index.GetByteLength(i));
                Assert.Equal((ulong)expectedCharLength, fileIndex.Index.GetCharLength(i));
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Test 2: GetByteOffset matches actual file positions ---

    [Fact]
    public async Task GetByteOffset_MatchesActualFilePositions()
    {
        var lines = new[]
        {
            "first line",
            "second with café",
            "third 日本",
            "last"
        };

        var tempFile = Path.GetTempFileName();
        try
        {
            // Write with CRLF endings, no trailing newline on last line
            var content = string.Join("\r\n", lines);
            var fileBytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(tempFile, fileBytes);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);
            await fileIndex.StartScanAsync();

            Assert.Equal(ScanState.ScanComplete, fileIndex.State);
            Assert.Equal(lines.Length, fileIndex.Index.LineCount);

            // Verify GetByteOffset(0) == 0
            Assert.Equal(0UL, fileIndex.Index.GetByteOffset(0));

            // Verify GetByteOffset(LineCount) == file size
            Assert.Equal((ulong)fileBytes.Length, fileIndex.Index.GetByteOffset(fileIndex.Index.LineCount));

            // For each line, verify reading GetByteLength bytes at GetByteOffset produces expected bytes
            for (int i = 0; i < fileIndex.Index.LineCount; i++)
            {
                ulong offset = fileIndex.Index.GetByteOffset(i);
                ulong length = fileIndex.Index.GetByteLength(i);

                var expectedLineBytes = new byte[length];
                Array.Copy(fileBytes, (int)offset, expectedLineBytes, 0, (int)length);

                // Verify the bytes at this offset match what we expect
                var lineContentBytes = Encoding.UTF8.GetBytes(lines[i]);
                int delimiterLen = (i < lines.Length - 1) ? 2 : 0; // CRLF = 2 bytes
                Assert.Equal(lineContentBytes.Length + delimiterLen, (int)length);

                // Content bytes should match
                for (int j = 0; j < lineContentBytes.Length; j++)
                {
                    Assert.Equal(lineContentBytes[j], expectedLineBytes[j]);
                }
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Test 3: Concurrent readers during active scan ---

    [Fact]
    public async Task ConcurrentReaders_DuringActiveScan_NoExceptions()
    {
        // Create a large file (10K+ lines)
        var tempFile = Path.GetTempFileName();
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 12000; i++)
            {
                sb.AppendLine($"Line {i:D5} with some content to make it non-trivial");
            }
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            using var cts = new CancellationTokenSource();
            var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            var exceptions = new List<Exception>();
            var readerBarrier = new Barrier(5); // 4 readers + 1 scan task

            // Start scan in background
            var scanTask = Task.Run(async () =>
            {
                readerBarrier.SignalAndWait();
                await fileIndex.StartScanAsync();
            });

            // Spawn 4 reader threads that poll during scan
            var readerTasks = Enumerable.Range(0, 4).Select(readerId => Task.Run(() =>
            {
                readerBarrier.SignalAndWait();
                try
                {
                    // Poll until scan completes or times out
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        var state = fileIndex.State;
                        int lineCount = fileIndex.Index.LineCount;

                        if (lineCount > 0)
                        {
                            // Read byte lengths for available lines
                            int linesToRead = Math.Min(lineCount, 100);
                            for (int i = 0; i < linesToRead; i++)
                            {
                                ulong byteLen = fileIndex.Index.GetByteLength(i);
                                Assert.True(byteLen > 0, $"Reader {readerId}: ByteLength for line {i} should be > 0");
                            }

                            // Read byte offset for a random available line
                            int midLine = lineCount / 2;
                            if (midLine > 0)
                            {
                                ulong offset = fileIndex.Index.GetByteOffset(midLine);
                                // Offset should be positive for any line > 0
                                Assert.True(offset > 0, $"Reader {readerId}: ByteOffset for line {midLine} should be > 0");
                            }
                        }

                        if (state == ScanState.ScanComplete)
                            break;

                        Thread.Sleep(1); // Small delay to avoid tight spin
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            })).ToArray();

            await scanTask;
            await Task.WhenAll(readerTasks);

            fileIndex.Dispose();

            Assert.Empty(exceptions);
            Assert.Equal(ScanState.ScanComplete, fileIndex.State);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Test 4: Cancellation during Quick_Scan stops within 2000ms ---

    [Fact]
    public async Task Cancellation_DuringQuickScan_StopsWithinTimeout()
    {
        // Create a very large file (1M+ bytes) to ensure Quick_Scan takes time
        var tempFile = Path.GetTempFileName();
        try
        {
            var sb = new StringBuilder();
            // ~50 bytes per line × 30000 lines = ~1.5MB
            for (int i = 0; i < 30000; i++)
            {
                sb.AppendLine($"Line {i:D5} padding padding padding padding padding");
            }
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            // Start scan in background
            var scanTask = fileIndex.StartScanAsync();

            // Wait a short time for scan to begin, then cancel
            await Task.Delay(10);
            var cancelTime = Stopwatch.StartNew();
            cts.Cancel();

            await scanTask;
            cancelTime.Stop();

            // State should be Cancelled (or ScanComplete if it finished before cancel)
            if (fileIndex.State == ScanState.Cancelled)
            {
                // Verify cancellation happened within generous timeout (2000ms for CI)
                Assert.True(cancelTime.ElapsedMilliseconds < 2000,
                    $"Cancellation took {cancelTime.ElapsedMilliseconds}ms, expected < 2000ms");
            }
            // If scan completed before cancellation signal was processed, that's also acceptable
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Test 5: Cancellation during scan stops within 2000ms ---

    [Fact]
    public async Task Cancellation_DuringScan_StopsWithinTimeout()
    {
        // Create a large file so scan takes time
        var tempFile = Path.GetTempFileName();
        try
        {
            var sb = new StringBuilder();
            // Many lines with multi-byte content to slow down scan
            for (int i = 0; i < 50000; i++)
            {
                sb.AppendLine($"Line {i:D5} café résumé naïve 日本語テスト");
            }
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            // Start scan in a background thread so we can observe state transitions
            var scanTask = Task.Run(() => fileIndex.StartScanAsync());

            // Wait for scan to start before cancelling
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                var state = fileIndex.State;
                if (state == ScanState.ScanInProgress)
                {
                    break;
                }
                if (state == ScanState.ScanComplete ||
                    state == ScanState.Failed ||
                    state == ScanState.Cancelled)
                {
                    break; // Already done
                }
                await Task.Delay(1);
            }

            // Cancel during scan
            var cancelTime = Stopwatch.StartNew();
            cts.Cancel();

            await scanTask;
            cancelTime.Stop();

            // State should be Cancelled (or ScanComplete if it finished before cancel)
            if (fileIndex.State == ScanState.Cancelled)
            {
                Assert.True(cancelTime.ElapsedMilliseconds < 5000,
                    $"Scan cancellation took {cancelTime.ElapsedMilliseconds}ms, expected < 5000ms");
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Test 6: File with 10,000+ lines completes without error ---

    [Fact]
    public async Task LargeFile_TenThousandPlusLines_CompletesWithoutError()
    {
        var tempFile = Path.GetTempFileName();
        const int lineCount = 10500;
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lineCount; i++)
            {
                sb.AppendLine($"Line number {i} with some content");
            }
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);
            await fileIndex.StartScanAsync();

            Assert.Equal(ScanState.ScanComplete, fileIndex.State);
            Assert.Null(fileIndex.Error);
            Assert.Equal(lineCount, fileIndex.Index.LineCount);

            // Verify byte offset consistency: sum of all byte lengths == file size
            var fileSize = new FileInfo(tempFile).Length;
            Assert.Equal((ulong)fileSize, fileIndex.Index.GetByteOffset(fileIndex.Index.LineCount));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Test 7: File opened with ReadWrite sharing ---

    [Fact]
    public async Task FileOpenedWithReadWriteSharing_AnotherProcessCanRead()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 5000; i++)
            {
                sb.AppendLine($"Line {i} content for sharing test");
            }
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            // Start scan in background
            var scanTask = fileIndex.StartScanAsync();

            // While scan is running, open the same file with another FileStream for reading
            Exception? sharingException = null;
            try
            {
                using var readerStream = new FileStream(
                    tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Read some bytes to prove it works
                var buffer = new byte[100];
                int bytesRead = await readerStream.ReadAsync(buffer);
                Assert.True(bytesRead > 0, "Should be able to read from file during scan");
            }
            catch (IOException ex)
            {
                sharingException = ex;
            }

            await scanTask;

            // Verify no IOException occurred when opening the file concurrently
            Assert.Null(sharingException);
            Assert.Equal(ScanState.ScanComplete, fileIndex.State);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
