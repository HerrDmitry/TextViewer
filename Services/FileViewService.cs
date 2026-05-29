using System.Text;
using Microsoft.Extensions.Logging;

namespace TextViewer.Services;

public sealed class FileViewService : IDisposable
{
    private readonly string _filePath;
    private readonly FileIndex _fileIndex;
    private readonly ILogger<FileViewService> _logger;
    private readonly CancellationToken _serviceCancellationToken;
    private readonly Task _scanTask;

    public FileViewService(string filePath, CancellationToken cancellationToken, ILogger<FileViewService> logger)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceCancellationToken = cancellationToken;

        var fileIndexLogger = new TypedLoggerAdapter<FileIndex>(logger);
        _fileIndex = new FileIndex(filePath, cancellationToken, fileIndexLogger);

        // Start scan — store task for observation in Dispose
        _scanTask = _fileIndex.StartScanAsync();
    }

    /// <summary>Reflects FileIndex.State for lifecycle observation.</summary>
    public ScanState ScanState => _fileIndex.State;

    /// <summary>
    /// Extracts a rectangular view region from the file.
    /// Opens an independent file handle per call for concurrent safety.
    /// </summary>
    public Task<Result<ViewResult, ViewError>> GetViewAsync(
        int startLine, int startCol, int rowCount, int colCount,
        CancellationToken cancellationToken = default)
    {
        // Validate parameters before any file I/O or FileIndex lookup
        if (startLine < 0)
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.InvalidParameter, "Start_Line must be >= 0")));

        if (startCol < 0)
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.InvalidParameter, "Start_Column must be >= 0")));

        if (rowCount < 1)
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.InvalidParameter, "Row_Count must be >= 1")));

        if (colCount < 1)
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.InvalidParameter, "Column_Count must be >= 1")));

        // Check cancellation (linked: service-level + per-request)
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCancellationToken, cancellationToken);
        var ct = linkedCts.Token;
        ct.ThrowIfCancellationRequested();

        // Check if FileIndex is in Failed state before opening handle
        if (_fileIndex.State == ScanState.Failed)
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.FileNotAccessible, $"File index failed: {_filePath}")));

        // Snapshot line count (volatile read) and scan state
        var scannedLines = _fileIndex.Index.LineCount;
        var scanComplete = _fileIndex.State >= ScanState.QuickScanComplete
                           && _fileIndex.State < ScanState.Failed;

        // Edge case: empty file after scan complete
        if (scanComplete && scannedLines == 0)
            return Task.FromResult(Result<ViewResult, ViewError>.Success(
                new ViewResult(new[] { "" })));

        // Edge case: startLine beyond file after scan complete
        if (scanComplete && startLine >= scannedLines)
            return Task.FromResult(Result<ViewResult, ViewError>.Success(
                new ViewResult(new[] { "" })));

        // Row extraction
        var rows = new List<string>();
        FileStream? stream = null;
        try
        {
            stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var encoding = _fileIndex.Encoding;
            var bomByteLength = _fileIndex.BomByteLength;

            for (int i = 0; i < rowCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                int lineIdx = startLine + i;

                if (scanComplete && lineIdx >= scannedLines)
                    break;

                if (!scanComplete && lineIdx >= scannedLines)
                {
                    rows.Add("");
                    continue;
                }

                var byteOffset = _fileIndex.Index.GetByteOffset(lineIdx);
                var byteLen = (int)_fileIndex.Index.GetByteLength(lineIdx);

                // Read line bytes
                stream.Seek((long)byteOffset, SeekOrigin.Begin);
                var lineBytes = new byte[byteLen];
                int totalRead = 0;
                while (totalRead < byteLen)
                {
                    int read = stream.Read(lineBytes, totalRead, byteLen - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }

                // Decode line bytes using the partial decode helper
                int bomSkip = (lineIdx == 0) ? bomByteLength : 0;
                int charsNeeded = startCol + colCount;
                var (content, delimiter) = DecodeUpTo(lineBytes, totalRead, encoding, bomSkip, charsNeeded);

                // Apply column slicing: skip startCol chars, take up to colCount chars
                if (startCol >= content.Length)
                {
                    rows.Add(delimiter);
                }
                else
                {
                    int end = Math.Min(startCol + colCount, content.Length);
                    rows.Add(content.Substring(startCol, end - startCol) + delimiter);
                }
            }
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.FileNotAccessible, $"File not accessible: {_filePath}: FileNotFoundException")));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.IoError, $"Read error: {_filePath}: UnauthorizedAccessException")));
        }
        catch (IOException)
        {
            return Task.FromResult(Result<ViewResult, ViewError>.Failure(
                new ViewError(ViewErrorCode.IoError, $"Read error: {_filePath}: IOException")));
        }
        finally
        {
            stream?.Dispose();
        }

        // Ensure non-empty result
        if (rows.Count == 0)
            rows.Add("");

        return Task.FromResult(Result<ViewResult, ViewError>.Success(new ViewResult(rows)));
    }

    public void Dispose()
    {
        _fileIndex.Dispose();

        // Observe scan task — log if it faulted
        if (_scanTask.IsFaulted)
        {
            _logger.LogError(_scanTask.Exception!.InnerException ?? _scanTask.Exception,
                "FileIndex scan faulted for {FilePath}", _filePath);
        }

        _logger.LogDebug("FileViewService disposed for {FilePath}", _filePath);
    }

    /// <summary>
    /// Decodes line bytes up to a maximum number of characters, returning the content and delimiter separately.
    /// Uses a streaming Decoder with DecoderReplacementFallback for invalid byte sequences (→ U+FFFD).
    /// For multi-byte encodings (UTF-16, UTF-32), delimiter detection accounts for the encoding's byte width.
    /// </summary>
    /// <param name="lineBytes">The raw bytes of the line (including delimiter and possibly BOM on first line).</param>
    /// <param name="totalBytesRead">The actual number of bytes read into lineBytes.</param>
    /// <param name="encoding">The file's detected encoding.</param>
    /// <param name="bomSkip">Number of BOM bytes to skip (non-zero only for the first line).</param>
    /// <param name="charsNeeded">Maximum number of content characters to decode (startCol + colCount).</param>
    /// <returns>A tuple of (content, delimiter) where content has at most charsNeeded characters.</returns>
    internal static (string content, string delimiter) DecodeUpTo(
        byte[] lineBytes, int totalBytesRead, Encoding encoding, int bomSkip, int charsNeeded)
    {
        if (totalBytesRead == 0)
            return ("", "");

        // 1. Determine delimiter bytes at end of line.
        //    For multi-byte encodings, delimiters are encoded in that encoding's byte width.
        int delimiterByteCount = GetDelimiterByteCount(lineBytes, totalBytesRead, encoding);
        int contentByteLen = totalBytesRead - delimiterByteCount;

        // 2. Skip BOM bytes on first line
        int contentStart = bomSkip;
        int remaining = contentByteLen - bomSkip;
        if (remaining < 0) remaining = 0;

        // 3. Streaming decode with Decoder (maintains state across chunks, uses replacement fallback)
        var decoderEncoding = Encoding.GetEncoding(
            encoding.CodePage,
            EncoderFallback.ReplacementFallback,
            new DecoderReplacementFallback("\uFFFD"));
        var decoder = decoderEncoding.GetDecoder();

        string content;
        if (remaining == 0 || charsNeeded <= 0)
        {
            content = "";
        }
        else
        {
            const int ChunkSize = 4096;
            var contentBuilder = new StringBuilder();
            int charsDecoded = 0;
            int readOffset = contentStart;

            while (remaining > 0 && charsDecoded < charsNeeded)
            {
                int bytesToProcess = Math.Min(remaining, ChunkSize);
                bool flush = (remaining == bytesToProcess); // flush on last chunk

                int charCount = decoder.GetCharCount(lineBytes, readOffset, bytesToProcess, flush);
                var charBuf = new char[charCount];
                decoder.GetChars(lineBytes, readOffset, bytesToProcess, charBuf, 0, flush);

                int take = Math.Min(charCount, charsNeeded - charsDecoded);
                contentBuilder.Append(charBuf, 0, take);
                charsDecoded += take;

                readOffset += bytesToProcess;
                remaining -= bytesToProcess;
            }

            content = contentBuilder.ToString();
        }

        // 4. Decode delimiter string from the delimiter bytes using the file's encoding
        string delimiter = delimiterByteCount > 0
            ? encoding.GetString(lineBytes, contentByteLen, delimiterByteCount)
            : "";

        return (content, delimiter);
    }

    /// <summary>
    /// Determines the number of delimiter bytes at the end of a line's byte buffer,
    /// accounting for multi-byte encodings (UTF-16, UTF-32) where delimiters are
    /// encoded in the encoding's byte width.
    /// Returns the byte count of the delimiter (e.g., 2 for CRLF in single-byte,
    /// 4 for CRLF in UTF-16, 8 for CRLF in UTF-32).
    /// </summary>
    private static int GetDelimiterByteCount(byte[] buffer, int length, Encoding encoding)
    {
        if (length == 0)
            return 0;

        int bytesPerChar = GetBytesPerCodeUnit(encoding);

        if (bytesPerChar == 1)
        {
            // Single-byte encodings (UTF-8, ASCII, etc.)
            if (length >= 2 && buffer[length - 2] == 0x0D && buffer[length - 1] == 0x0A)
                return 2; // CRLF
            if (buffer[length - 1] == 0x0A)
                return 1; // LF
            if (buffer[length - 1] == 0x0D)
                return 1; // CR
            return 0;
        }

        if (bytesPerChar == 2)
        {
            // UTF-16 (LE or BE)
            bool isLittleEndian = encoding.CodePage == 1200; // UTF-16 LE

            // Check for CRLF (2 code units = 4 bytes)
            if (length >= 4)
            {
                byte crLow, crHigh, lfLow, lfHigh;
                if (isLittleEndian)
                {
                    crLow = buffer[length - 4]; crHigh = buffer[length - 3];
                    lfLow = buffer[length - 2]; lfHigh = buffer[length - 1];
                }
                else
                {
                    crHigh = buffer[length - 4]; crLow = buffer[length - 3];
                    lfHigh = buffer[length - 2]; lfLow = buffer[length - 1];
                }

                if (crLow == 0x0D && crHigh == 0x00 && lfLow == 0x0A && lfHigh == 0x00)
                    return 4; // CRLF in UTF-16
            }

            // Check for LF or CR (1 code unit = 2 bytes)
            if (length >= 2)
            {
                byte low, high;
                if (isLittleEndian)
                {
                    low = buffer[length - 2]; high = buffer[length - 1];
                }
                else
                {
                    high = buffer[length - 2]; low = buffer[length - 1];
                }

                if (low == 0x0A && high == 0x00)
                    return 2; // LF in UTF-16
                if (low == 0x0D && high == 0x00)
                    return 2; // CR in UTF-16
            }

            return 0;
        }

        if (bytesPerChar == 4)
        {
            // UTF-32 (LE or BE)
            bool isLittleEndian = encoding.CodePage == 12000; // UTF-32 LE

            // Check for CRLF (2 code units = 8 bytes)
            if (length >= 8)
            {
                uint crVal = ReadUInt32(buffer, length - 8, isLittleEndian);
                uint lfVal = ReadUInt32(buffer, length - 4, isLittleEndian);

                if (crVal == 0x0D && lfVal == 0x0A)
                    return 8; // CRLF in UTF-32
            }

            // Check for LF or CR (1 code unit = 4 bytes)
            if (length >= 4)
            {
                uint val = ReadUInt32(buffer, length - 4, isLittleEndian);

                if (val == 0x0A)
                    return 4; // LF in UTF-32
                if (val == 0x0D)
                    return 4; // CR in UTF-32
            }

            return 0;
        }

        // Fallback for unknown encodings: treat as single-byte
        if (length >= 2 && buffer[length - 2] == 0x0D && buffer[length - 1] == 0x0A)
            return 2;
        if (buffer[length - 1] == 0x0A)
            return 1;
        if (buffer[length - 1] == 0x0D)
            return 1;
        return 0;
    }

    /// <summary>
    /// Returns the number of bytes per code unit for the given encoding.
    /// UTF-8/ASCII = 1, UTF-16 = 2, UTF-32 = 4.
    /// </summary>
    private static int GetBytesPerCodeUnit(Encoding encoding)
    {
        // UTF-32 LE (codepage 12000) or UTF-32 BE (codepage 12001)
        if (encoding.CodePage == 12000 || encoding.CodePage == 12001)
            return 4;

        // UTF-16 LE (codepage 1200) or UTF-16 BE (codepage 1201)
        if (encoding.CodePage == 1200 || encoding.CodePage == 1201)
            return 2;

        // UTF-8, ASCII, and other single-byte encodings
        return 1;
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer from a byte array at the given offset,
    /// respecting endianness.
    /// </summary>
    private static uint ReadUInt32(byte[] buffer, int offset, bool littleEndian)
    {
        if (littleEndian)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }
        else
        {
            return (uint)((buffer[offset] << 24)
                | (buffer[offset + 1] << 16)
                | (buffer[offset + 2] << 8)
                | buffer[offset + 3]);
        }
    }

    /// <summary>
    /// Adapts an ILogger of one category to ILogger of another category.
    /// Used to provide FileIndex with a typed logger from the service's logger.
    /// </summary>
    private sealed class TypedLoggerAdapter<T> : ILogger<T>
    {
        private readonly ILogger _inner;

        public TypedLoggerAdapter(ILogger inner)
        {
            _inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel)
            => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
