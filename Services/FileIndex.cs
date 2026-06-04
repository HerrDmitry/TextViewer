using System.Text;
using Microsoft.Extensions.Logging;

namespace TextViewer.Services;

/// <summary>
/// Scans a single file in a unified single pass to build a memory-compact,
/// thread-safe index of per-line metadata (byte lengths + char lengths).
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
    private long _bytesRead;

    public FileIndex(string filePath, CancellationToken cancellationToken, ILogger<FileIndex> logger)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _cancellationToken = cancellationToken;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Index = new LineIndex();
    }

    /// <summary>Thread-safe current scan state.</summary>
    public ScanState State => _state;

    /// <summary>Thread-safe error description (null when no error).</summary>
    public string? Error => _error;

    /// <summary>Thread-safe line index (readable after ScanComplete).</summary>
    public LineIndex Index { get; }

    /// <summary>Total file size in bytes (set before scan loop starts).</summary>
    public long TotalFileSize { get; private set; }

    /// <summary>Thread-safe bytes read so far during scan.</summary>
    public long BytesRead => Volatile.Read(ref _bytesRead);

    /// <summary>Detected file encoding (set during scan, defaults to UTF-8 when no BOM present).</summary>
    public Encoding Encoding { get; private set; } = Encoding.UTF8;

    /// <summary>Number of BOM bytes at the start of the file (0 if no BOM).</summary>
    public int BomByteLength { get; private set; } = 0;

    /// <summary>
    /// Starts the unified single-pass scan.
    /// Returns when scan completes, fails, or is cancelled.
    /// </summary>
    public async Task<Result<ScanSummary, ScanError>> StartScanAsync()
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
            return Result<ScanSummary, ScanError>.Failure(
                new ScanError(ScanErrorCode.FileNotFound, _error));
        }
        catch (UnauthorizedAccessException ex)
        {
            _error = $"Failed to open {_filePath}: UnauthorizedAccessException";
            _state = ScanState.Failed;
            _logger.LogError(ex, "Failed to open {FilePath}: UnauthorizedAccessException", _filePath);
            return Result<ScanSummary, ScanError>.Failure(
                new ScanError(ScanErrorCode.AccessDenied, _error));
        }
        catch (IOException ex)
        {
            _error = $"Failed to open {_filePath}: IOException";
            _state = ScanState.Failed;
            _logger.LogError(ex, "Failed to open {FilePath}: IOException", _filePath);
            return Result<ScanSummary, ScanError>.Failure(
                new ScanError(ScanErrorCode.IoError, _error));
        }

        // File opened — transition to ScanInProgress
        _state = ScanState.ScanInProgress;
        _logger.LogInformation("Unified scan started for {FilePath}", _filePath);

        // --- Unified scan ---
        try
        {
            await RunUnifiedScanAsync();
        }
        catch (OperationCanceledException)
        {
            Index.Clear();
            _state = ScanState.Cancelled;
            _logger.LogInformation("Scan cancelled for {FilePath}", _filePath);
            return Result<ScanSummary, ScanError>.Failure(
                new ScanError(ScanErrorCode.Cancelled, $"Scan cancelled for {_filePath}"));
        }
        catch (IOException ex)
        {
            Index.Clear();
            _error = $"Scan failed for {_filePath}: IOException";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: IOException", _filePath);
            return Result<ScanSummary, ScanError>.Failure(
                new ScanError(ScanErrorCode.IoError, _error));
        }
        catch (OutOfMemoryException ex)
        {
            Index.Clear();
            _error = $"Scan failed for {_filePath}: OutOfMemoryException";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: OutOfMemoryException", _filePath);
            return Result<ScanSummary, ScanError>.Failure(
                new ScanError(ScanErrorCode.OutOfMemory, _error));
        }
        catch (Exception ex)
        {
            Index.Clear();
            _error = $"Scan failed for {_filePath}: {ex.GetType().Name}";
            _state = ScanState.Failed;
            _logger.LogInformation(ex, "Scan failed for {FilePath}: {ExceptionType}", _filePath, ex.GetType().Name);
            return Result<ScanSummary, ScanError>.Failure(
                new ScanError(ScanErrorCode.Unknown, _error));
        }

        _state = ScanState.ScanComplete;
        _logger.LogInformation("Scan complete for {FilePath}", _filePath);

        return Result<ScanSummary, ScanError>.Success(
            new ScanSummary(Index.LineCount, Encoding, BomByteLength));
    }

    /// <summary>
    /// Unified single-pass scan: BOM detection + sequential line scanning with
    /// simultaneous byte length and char length computation.
    /// </summary>
    private async Task RunUnifiedScanAsync()
    {
        // Step 1: Detect BOM (read up to 4 bytes, set Encoding + BomByteLength)
        (Encoding encoding, int bomByteLength) = await DetectEncodingAsync();

        // Step 2: Create decoder with replacement fallback
        Encoding decoderEncoding = Encoding.GetEncoding(
            encoding.CodePage,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
        Decoder decoder = decoderEncoding.GetDecoder();

        // Step 3: Seek to start (BOM bytes included in first line's byte length,
        // but excluded from char count)
        TotalFileSize = _stream!.Length;
        _stream.Seek(0, SeekOrigin.Begin);

        // Step 4: Sequential read loop
        const int BufferSize = 65536; // 64KB
        const int BatchSize = 1000;

        var buffer = new byte[BufferSize];
        var batch = new List<LinePair>(BatchSize);

        // Accumulate content bytes for current line (excluding delimiter and BOM)
        var lineContentBytes = new MemoryStream();
        ulong currentLineBytes = 0; // total bytes for current line (content + delimiter + BOM on first)
        bool previousByteWasCR = false;
        int bomBytesRemaining = bomByteLength; // track BOM bytes to skip from content

        int bytesRead;
        while ((bytesRead = await _stream.ReadAsync(buffer.AsMemory(0, BufferSize), _cancellationToken)) > 0)
        {
            Volatile.Write(ref _bytesRead, Volatile.Read(ref _bytesRead) + bytesRead);

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
                        // Emit line pair (content already accumulated without CR)
                        ulong charLength = ComputeCharLength(decoder, lineContentBytes);
                        batch.Add(new LinePair(currentLineBytes, charLength));
                        currentLineBytes = 0;
                        lineContentBytes.SetLength(0);
                        decoder.Reset();

                        if (batch.Count >= BatchSize)
                        {
                            Index.AppendLinePairs(batch.ToArray());
                            batch.Clear();
                        }
                        continue;
                    }
                    else
                    {
                        // Standalone CR — emit line (content accumulated without CR)
                        ulong charLength = ComputeCharLength(decoder, lineContentBytes);
                        batch.Add(new LinePair(currentLineBytes, charLength));
                        currentLineBytes = 0;
                        lineContentBytes.SetLength(0);
                        decoder.Reset();

                        if (batch.Count >= BatchSize)
                        {
                            Index.AppendLinePairs(batch.ToArray());
                            batch.Clear();
                        }
                        // Fall through to process current byte 'b'
                    }
                }

                if (b == 0x0A)
                {
                    // LF delimiter
                    currentLineBytes += 1; // the LF byte
                    // Emit line pair
                    ulong charLength = ComputeCharLength(decoder, lineContentBytes);
                    batch.Add(new LinePair(currentLineBytes, charLength));
                    currentLineBytes = 0;
                    lineContentBytes.SetLength(0);
                    decoder.Reset();

                    if (batch.Count >= BatchSize)
                    {
                        Index.AppendLinePairs(batch.ToArray());
                        batch.Clear();
                    }
                }
                else if (b == 0x0D)
                {
                    // CR — might be start of CRLF
                    currentLineBytes += 1; // the CR byte
                    previousByteWasCR = true;
                }
                else
                {
                    // Regular content byte (or BOM byte)
                    currentLineBytes += 1;
                    if (bomBytesRemaining > 0)
                    {
                        // BOM byte: count in byte length but NOT in content for char decoding
                        bomBytesRemaining--;
                    }
                    else
                    {
                        lineContentBytes.WriteByte(b);
                    }
                }
            }

            // Check cancellation between buffer reads
            _cancellationToken.ThrowIfCancellationRequested();
        }

        // Step 5: Flush final line + remaining batch

        // Handle trailing CR at end of file (standalone CR as last byte)
        if (previousByteWasCR)
        {
            ulong charLength = ComputeCharLength(decoder, lineContentBytes);
            batch.Add(new LinePair(currentLineBytes, charLength));
            currentLineBytes = 0;
            lineContentBytes.SetLength(0);
        }

        // Handle final unterminated line
        if (currentLineBytes > 0)
        {
            ulong charLength = ComputeCharLength(decoder, lineContentBytes);
            batch.Add(new LinePair(currentLineBytes, charLength));
        }

        // Flush remaining batch
        if (batch.Count > 0)
        {
            Index.AppendLinePairs(batch.ToArray());
        }
    }

    /// <summary>
    /// Computes char length by decoding the accumulated content bytes using the decoder.
    /// </summary>
    private static ulong ComputeCharLength(Decoder decoder, MemoryStream contentBytes)
    {
        if (contentBytes.Length == 0)
            return 0;

        var span = contentBytes.GetBuffer().AsSpan(0, (int)contentBytes.Length);
        int charCount = decoder.GetCharCount(span, flush: true);
        return (ulong)charCount;
    }

    /// <summary>
    /// Detects the file encoding by reading the BOM from the stream.
    /// Returns the detected encoding and the BOM byte length (0 if no BOM found).
    /// Sets the public Encoding and BomByteLength properties before returning.
    /// Does NOT reset stream position — caller must seek after calling.
    /// </summary>
    private async Task<(Encoding encoding, int bomByteLength)> DetectEncodingAsync()
    {
        // Read up to 4 bytes for BOM detection
        byte[] bom = new byte[4];
        int read = await _stream!.ReadAsync(bom.AsMemory(0, 4), _cancellationToken);

        if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00)
        {
            Encoding = Encoding.UTF32; // UTF-32 LE
            BomByteLength = 4;
            return (Encoding, BomByteLength);
        }

        if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
        {
            Encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true); // UTF-32 BE
            BomByteLength = 4;
            return (Encoding, BomByteLength);
        }

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            Encoding = Encoding.UTF8; // UTF-8 BOM
            BomByteLength = 3;
            return (Encoding, BomByteLength);
        }

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
        {
            Encoding = Encoding.Unicode; // UTF-16 LE BOM
            BomByteLength = 2;
            return (Encoding, BomByteLength);
        }

        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
        {
            Encoding = Encoding.BigEndianUnicode; // UTF-16 BE BOM
            BomByteLength = 2;
            return (Encoding, BomByteLength);
        }

        // No BOM detected — default to UTF-8, no BOM bytes to skip
        Encoding = Encoding.UTF8;
        BomByteLength = 0;
        return (Encoding, BomByteLength);
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
