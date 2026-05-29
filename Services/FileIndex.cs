using System.Text;
using Microsoft.Extensions.Logging;

namespace TextViewer.Services;

/// <summary>
/// Scans a single file in two phases (Quick_Scan → Full_Scan) to build a
/// memory-compact, thread-safe index of per-line metadata.
/// Thread-safe for concurrent reads of State, Error, and Index properties.
/// </summary>
public sealed class FileIndex : IDisposable
{
    private readonly string _filePath;
    private readonly CancellationToken _cancellationToken;
    private readonly ILogger<FileIndex> _logger;
    private FileStream? _stream;
    private volatile ScanState _state = ScanState.NotStarted;
    private volatile string? _error;

    public FileIndex(string filePath, CancellationToken cancellationToken, ILogger<FileIndex> logger)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _cancellationToken = cancellationToken;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Index = new LineIndex();
    }

    /// <summary>Thread-safe current scan phase.</summary>
    public ScanState State => _state;

    /// <summary>Thread-safe error description (null when no error).</summary>
    public string? Error => _error;

    /// <summary>Thread-safe line index (readable after QuickScanComplete).</summary>
    public LineIndex Index { get; }

    /// <summary>
    /// Starts the two-phase scan. Quick_Scan runs first, then Full_Scan automatically.
    /// Returns when both phases complete, fail, or are cancelled.
    /// </summary>
    public async Task StartScanAsync()
    {
        _logger.LogInformation("Starting scan for {FilePath}", _filePath);

        // Attempt to open the file
        try
        {
            _stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
        }
        catch (FileNotFoundException ex)
        {
            _error = $"Failed to open {_filePath}: FileNotFoundException";
            _state = ScanState.Failed;
            _logger.LogError(ex, "Failed to open {FilePath}: FileNotFoundException", _filePath);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _error = $"Failed to open {_filePath}: UnauthorizedAccessException";
            _state = ScanState.Failed;
            _logger.LogError(ex, "Failed to open {FilePath}: UnauthorizedAccessException", _filePath);
            return;
        }
        catch (IOException ex)
        {
            _error = $"Failed to open {_filePath}: IOException";
            _state = ScanState.Failed;
            _logger.LogError(ex, "Failed to open {FilePath}: IOException", _filePath);
            return;
        }

        // File opened successfully — transition to QuickScanInProgress
        _state = ScanState.QuickScanInProgress;
        _logger.LogInformation("Quick_Scan started for {FilePath}", _filePath);

        // --- Quick_Scan phase ---
        try
        {
            await RunQuickScanAsync();
        }
        catch (OperationCanceledException)
        {
            Index.Clear();
            _state = ScanState.Cancelled;
            _logger.LogInformation("Quick_Scan cancelled for {FilePath}", _filePath);
            return;
        }
        catch (IOException ex)
        {
            Index.Clear();
            _error = $"Scan failed for {_filePath}: IOException";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: IOException", _filePath);
            return;
        }
        catch (OutOfMemoryException ex)
        {
            Index.Clear();
            _error = $"Scan failed for {_filePath}: OutOfMemoryException";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: OutOfMemoryException", _filePath);
            return;
        }

        _state = ScanState.QuickScanComplete;
        _logger.LogInformation("Quick_Scan complete for {FilePath}", _filePath);

        // --- Full_Scan phase ---
        _state = ScanState.FullScanInProgress;
        _logger.LogInformation("Full_Scan started for {FilePath}", _filePath);

        try
        {
            await RunFullScanAsync();
        }
        catch (OperationCanceledException)
        {
            // Quick_Scan data preserved — do NOT clear LineIndex
            _state = ScanState.Cancelled;
            _logger.LogInformation("Full_Scan cancelled for {FilePath}", _filePath);
            return;
        }
        catch (OutOfMemoryException ex)
        {
            _error = $"Scan failed for {_filePath}: OutOfMemoryException";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: OutOfMemoryException", _filePath);
            return;
        }
        catch (IOException ex)
        {
            _error = $"Scan failed for {_filePath}: IOException";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: IOException", _filePath);
            return;
        }
        catch (Exception ex)
        {
            _error = $"Scan failed for {_filePath}: {ex.GetType().Name}";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: {ExceptionType}", _filePath, ex.GetType().Name);
            return;
        }

        Index.FinalizeCharLengths();
        _state = ScanState.FullScanComplete;
        _logger.LogInformation("Full_Scan complete for {FilePath}", _filePath);
    }

    private async Task RunQuickScanAsync()
    {
        const int BufferSize = 65536; // 64KB
        const int BatchSize = 1000;

        var buffer = new byte[BufferSize];
        var batch = new List<ulong>(BatchSize);
        ulong currentLineBytes = 0;
        bool previousByteWasCR = false;

        int bytesRead;
        while ((bytesRead = await _stream!.ReadAsync(buffer.AsMemory(0, BufferSize), _cancellationToken)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                byte b = buffer[i];

                if (previousByteWasCR)
                {
                    previousByteWasCR = false;
                    if (b == 0x0A)
                    {
                        // CRLF: CR was already counted, add LF byte
                        currentLineBytes += 1; // the LF byte
                        batch.Add(currentLineBytes);
                        currentLineBytes = 0;

                        if (batch.Count >= BatchSize)
                        {
                            Index.AppendByteLengths(batch.ToArray());
                            batch.Clear();
                        }
                        continue;
                    }
                    else
                    {
                        // Standalone CR — line already includes the CR byte
                        batch.Add(currentLineBytes);
                        currentLineBytes = 0;

                        if (batch.Count >= BatchSize)
                        {
                            Index.AppendByteLengths(batch.ToArray());
                            batch.Clear();
                        }
                        // Fall through to process current byte 'b'
                    }
                }

                if (b == 0x0A)
                {
                    // LF delimiter
                    currentLineBytes += 1; // the LF byte
                    batch.Add(currentLineBytes);
                    currentLineBytes = 0;

                    if (batch.Count >= BatchSize)
                    {
                        Index.AppendByteLengths(batch.ToArray());
                        batch.Clear();
                    }
                }
                else if (b == 0x0D)
                {
                    // CR — might be start of CRLF, peek at next byte
                    currentLineBytes += 1; // the CR byte
                    previousByteWasCR = true;
                }
                else
                {
                    // Regular content byte
                    currentLineBytes += 1;
                }
            }

            // Check cancellation between buffer reads
            _cancellationToken.ThrowIfCancellationRequested();
        }

        // Handle trailing CR at end of file (standalone CR as last byte)
        if (previousByteWasCR)
        {
            // The CR byte was already counted in currentLineBytes
            batch.Add(currentLineBytes);
            currentLineBytes = 0;
        }

        // Handle final unterminated line (content bytes without trailing delimiter)
        if (currentLineBytes > 0)
        {
            batch.Add(currentLineBytes);
        }

        // Flush remaining batch
        if (batch.Count > 0)
        {
            Index.AppendByteLengths(batch.ToArray());
        }
    }

    private async Task RunFullScanAsync()
    {
        int lineCount = Index.LineCount;
        if (lineCount == 0)
            return;

        // Seek stream back to beginning for Full_Scan
        _stream!.Seek(0, SeekOrigin.Begin);

        // Detect encoding from BOM by reading the first few bytes
        (Encoding encoding, int bomByteLength) = await DetectEncodingAsync();

        // Create a decoder with replacement fallback for invalid bytes
        Encoding decoderEncoding = Encoding.GetEncoding(
            encoding.CodePage,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);

        // Seek back to start for sequential reading
        _stream.Seek(0, SeekOrigin.Begin);

        // Use a single reusable buffer to avoid per-line allocations
        const int BufferSize = 65536;
        byte[] buffer = new byte[BufferSize];
        int bufferOffset = 0;
        int bufferFilled = 0;

        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            if (lineIndex % 1000 == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            int byteLength = (int)Index.GetByteLength(lineIndex);
            if (byteLength == 0)
            {
                Index.SetCharLength(lineIndex, 0);
                continue;
            }

            // We need to read the line bytes to know the delimiter, so read into buffer
            int contentLength = 0;
            int contentStart = 0;

            // For lines that fit in buffer, decode directly
            // For lines larger than buffer, accumulate via decoder
            if (byteLength <= BufferSize)
            {
                // Ensure we have enough bytes in buffer
                await EnsureBufferAsync(buffer, byteLength);
                
                int delimiterBytes = GetDelimiterByteCount(buffer, bufferOffset, byteLength);
                contentStart = bufferOffset;
                contentLength = byteLength - delimiterBytes;

                // Exclude BOM from first line
                if (lineIndex == 0 && bomByteLength > 0 && contentLength >= bomByteLength)
                {
                    contentStart += bomByteLength;
                    contentLength -= bomByteLength;
                }

                ulong charLength = 0;
                if (contentLength > 0)
                {
                    charLength = (ulong)decoderEncoding.GetCharCount(buffer, contentStart, contentLength);
                }
                Index.SetCharLength(lineIndex, charLength);
                bufferOffset += byteLength;
            }
            else
            {
                // Large line: read in chunks, count chars
                int remaining = byteLength;
                int charCount = 0;
                bool isFirstSegment = true;
                var decoder = decoderEncoding.GetDecoder();

                while (remaining > 0)
                {
                    await EnsureBufferAsync(buffer, Math.Min(remaining, BufferSize));
                    int chunkSize = Math.Min(remaining, bufferFilled - bufferOffset);

                    int start = bufferOffset;
                    int len = chunkSize;

                    // Last chunk: exclude delimiter
                    if (remaining == chunkSize)
                    {
                        int delimiterBytes = GetDelimiterByteCount(buffer, bufferOffset, chunkSize);
                        len -= delimiterBytes;
                    }

                    // First chunk of first line: exclude BOM
                    if (lineIndex == 0 && isFirstSegment && bomByteLength > 0 && len >= bomByteLength)
                    {
                        start += bomByteLength;
                        len -= bomByteLength;
                    }

                    if (len > 0)
                    {
                        bool flush = (remaining == chunkSize);
                        charCount += decoder.GetCharCount(buffer, start, len, flush);
                    }

                    bufferOffset += chunkSize;
                    remaining -= chunkSize;
                    isFirstSegment = false;
                }

                Index.SetCharLength(lineIndex, (ulong)charCount);
            }
        }

        // Local helper: ensure buffer has at least 'needed' bytes available from bufferOffset
        async Task EnsureBufferAsync(byte[] buf, int needed)
        {
            int available = bufferFilled - bufferOffset;
            if (available >= needed)
                return;

            // Shift remaining bytes to start of buffer
            if (available > 0)
            {
                Buffer.BlockCopy(buf, bufferOffset, buf, 0, available);
            }
            bufferOffset = 0;
            bufferFilled = available;

            // Fill buffer
            while (bufferFilled < needed)
            {
                int read = await _stream.ReadAsync(
                    buf.AsMemory(bufferFilled, buf.Length - bufferFilled),
                    _cancellationToken);
                if (read == 0)
                    break;
                bufferFilled += read;
            }
        }
    }

    /// <summary>
    /// Detects the file encoding by reading the BOM from the stream.
    /// Returns the detected encoding and the BOM byte length (0 if no BOM found).
    /// Does NOT reset stream position — caller must seek after calling.
    /// </summary>
    private async Task<(Encoding encoding, int bomByteLength)> DetectEncodingAsync()
    {
        // Read up to 4 bytes for BOM detection
        byte[] bom = new byte[4];
        int read = await _stream!.ReadAsync(bom.AsMemory(0, 4), _cancellationToken);

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return (Encoding.UTF8, 3); // UTF-8 BOM: 3 bytes

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return (Encoding.Unicode, 2); // UTF-16 LE BOM: 2 bytes

        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2); // UTF-16 BE BOM: 2 bytes

        // No BOM detected — default to UTF-8, no BOM bytes to skip
        return (Encoding.UTF8, 0);
    }

    /// <summary>
    /// Determines the number of delimiter bytes at the end of a line's byte buffer.
    /// Returns 2 for CRLF, 1 for LF or CR, 0 for no delimiter (final unterminated line).
    /// </summary>
    private static int GetDelimiterByteCount(byte[] buffer, int offset, int length)
    {
        if (length == 0)
            return 0;

        int end = offset + length;

        // Check for CRLF (last two bytes are 0x0D 0x0A)
        if (length >= 2 && buffer[end - 2] == 0x0D && buffer[end - 1] == 0x0A)
            return 2;

        // Check for LF
        if (buffer[end - 1] == 0x0A)
            return 1;

        // Check for CR
        if (buffer[end - 1] == 0x0D)
            return 1;

        // No delimiter (final unterminated line)
        return 0;
    }

    /// <summary>
    /// Releases all resources held by this FileIndex instance.
    /// </summary>
    public void Dispose()
    {
        // Close file stream
        try
        {
            _stream?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close file stream for {FilePath}", _filePath);
        }

        // Clear index memory
        try
        {
            Index.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear index for {FilePath}", _filePath);
        }

        _logger.LogDebug("FileIndex disposed for {FilePath}", _filePath);
    }
}
