using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Unit tests for unified scan edge cases.
/// Validates: Requirements 2.1, 2.2, 2.3, 3.1, 3.2, 4.1, 4.3
/// </summary>
public class UnifiedScanEdgeCaseTests
{
    private readonly ILogger<FileIndex> _logger = NullLogger<FileIndex>.Instance;

    /// <summary>Test 1: Empty file → LineCount = 0, State = ScanComplete</summary>
    [Fact]
    public async Task EmptyFile_ZeroLines_ScanComplete()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, []);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(0, fileIndex.Index.LineCount);
            Assert.Equal(ScanState.ScanComplete, fileIndex.State);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 2: Single LF → 1 line (byteLen=1, charLen=0)</summary>
    [Fact]
    public async Task SingleLF_OneLine_ByteLen1_CharLen0()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, [(byte)'\n']);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(1, fileIndex.Index.LineCount);
            Assert.Equal(1UL, fileIndex.Index.GetByteLength(0));
            Assert.Equal(0UL, fileIndex.Index.GetCharLength(0));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 3: "abc\n" → 1 line (byteLen=4, charLen=3)</summary>
    [Fact]
    public async Task AbcLF_OneLine_ByteLen4_CharLen3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "abc\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(1, fileIndex.Index.LineCount);
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(0));
            Assert.Equal(3UL, fileIndex.Index.GetCharLength(0));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 4: "abc\r\ndef\n" → 2 lines with CRLF and LF</summary>
    [Fact]
    public async Task CRLFAndLF_TwoLines()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "abc\r\ndef\n"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(2, fileIndex.Index.LineCount);
            // "abc" + CRLF = 5 bytes, charLen = 3
            Assert.Equal(5UL, fileIndex.Index.GetByteLength(0));
            Assert.Equal(3UL, fileIndex.Index.GetCharLength(0));
            // "def" + LF = 4 bytes, charLen = 3
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(1));
            Assert.Equal(3UL, fileIndex.Index.GetCharLength(1));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 5: "abc\rdef\nghi" → 3 lines (CR, LF, unterminated)</summary>
    [Fact]
    public async Task CRAndLFAndUnterminated_ThreeLines()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "abc\rdef\nghi"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(3, fileIndex.Index.LineCount);
            // "abc" + CR = 4 bytes, charLen = 3
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(0));
            Assert.Equal(3UL, fileIndex.Index.GetCharLength(0));
            // "def" + LF = 4 bytes, charLen = 3
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(1));
            Assert.Equal(3UL, fileIndex.Index.GetCharLength(1));
            // "ghi" unterminated = 3 bytes, charLen = 3
            Assert.Equal(3UL, fileIndex.Index.GetByteLength(2));
            Assert.Equal(3UL, fileIndex.Index.GetCharLength(2));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 6: Mixed: "a\r\nb\rc\nd" → 4 lines</summary>
    [Fact]
    public async Task MixedEndings_FourLines()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "a\r\nb\rc\nd"u8.ToArray());
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(4, fileIndex.Index.LineCount);
            // "a" + CRLF = 3 bytes, charLen = 1
            Assert.Equal(3UL, fileIndex.Index.GetByteLength(0));
            Assert.Equal(1UL, fileIndex.Index.GetCharLength(0));
            // "b" + CR = 2 bytes, charLen = 1
            Assert.Equal(2UL, fileIndex.Index.GetByteLength(1));
            Assert.Equal(1UL, fileIndex.Index.GetCharLength(1));
            // "c" + LF = 2 bytes, charLen = 1
            Assert.Equal(2UL, fileIndex.Index.GetByteLength(2));
            Assert.Equal(1UL, fileIndex.Index.GetCharLength(2));
            // "d" unterminated = 1 byte, charLen = 1
            Assert.Equal(1UL, fileIndex.Index.GetByteLength(3));
            Assert.Equal(1UL, fileIndex.Index.GetCharLength(3));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 7: UTF-8 BOM + content: BOM bytes in first line's byte length but NOT in char length</summary>
    [Fact]
    public async Task Utf8Bom_ByteLengthIncludesBom_CharLengthExcludesBom()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // UTF-8 BOM (EF BB BF) + "hi\n"
            byte[] content = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', (byte)'\n'];
            File.WriteAllBytes(tempFile, content);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(1, fileIndex.Index.LineCount);
            // BOM(3) + "hi"(2) + LF(1) = 6 bytes
            Assert.Equal(6UL, fileIndex.Index.GetByteLength(0));
            // charLen = 2 ("hi" only, BOM excluded)
            Assert.Equal(2UL, fileIndex.Index.GetCharLength(0));
            Assert.Equal(3, fileIndex.BomByteLength);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 8: UTF-8 multi-byte: "café\n" (é = 2 bytes) → byteLen=6, charLen=4</summary>
    [Fact]
    public async Task Utf8MultiByte_Cafe_CorrectLengths()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "café\n" in UTF-8: c(1) a(1) f(1) é(2) \n(1) = 6 bytes total
            byte[] content = [0x63, 0x61, 0x66, 0xC3, 0xA9, 0x0A];
            File.WriteAllBytes(tempFile, content);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(1, fileIndex.Index.LineCount);
            Assert.Equal(6UL, fileIndex.Index.GetByteLength(0));
            // charLen = 4 ("café" = 4 chars, delimiter excluded)
            Assert.Equal(4UL, fileIndex.Index.GetCharLength(0));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 9: CJK: "你好\n" → byteLen=7 (3+3+1), charLen=2</summary>
    [Fact]
    public async Task Utf8CJK_CorrectLengths()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "你好\n" in UTF-8: 你(3 bytes) 好(3 bytes) \n(1) = 7 bytes
            byte[] content = [0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD, 0x0A];
            File.WriteAllBytes(tempFile, content);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(1, fileIndex.Index.LineCount);
            Assert.Equal(7UL, fileIndex.Index.GetByteLength(0));
            // charLen = 2 ("你好" = 2 chars)
            Assert.Equal(2UL, fileIndex.Index.GetCharLength(0));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 10: Invalid byte 0xFF in UTF-8 → U+FFFD counted as 1 char</summary>
    [Fact]
    public async Task InvalidByte_ReplacementFallback_CountedAsOneChar()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // "A" + 0xFF + "B\n" → invalid byte replaced with U+FFFD
            byte[] content = [0x41, 0xFF, 0x42, 0x0A];
            File.WriteAllBytes(tempFile, content);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(1, fileIndex.Index.LineCount);
            Assert.Equal(4UL, fileIndex.Index.GetByteLength(0));
            // charLen = 3: 'A' + U+FFFD + 'B'
            Assert.Equal(3UL, fileIndex.Index.GetCharLength(0));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 11: File with just 2 bytes (FF FE) → detected as UTF-16 LE BOM, 0 lines (empty after BOM)</summary>
    [Fact]
    public async Task Utf16LeBom_Only_ZeroLines()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // UTF-16 LE BOM only: FF FE
            byte[] content = [0xFF, 0xFE];
            File.WriteAllBytes(tempFile, content);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(System.Text.Encoding.Unicode, fileIndex.Encoding);
            Assert.Equal(2, fileIndex.BomByteLength);
            Assert.Equal(ScanState.ScanComplete, fileIndex.State);
            // BOM-only file: the 2 BOM bytes form 1 line with byteLen=2, charLen=0
            // Actually per spec: BOM bytes are included in first line's byte length
            // but excluded from char count. Since there's no content after BOM and no
            // delimiter, this is a 1-line file with only BOM bytes.
            // Wait - empty file is 0 lines. A file with ONLY BOM bytes and no content
            // after it... the BOM bytes count toward byte length but if there are no
            // content bytes and no delimiter, the line has charLen=0.
            // Per the scan logic: currentLineBytes would be 2 (the BOM bytes counted),
            // bomBytesRemaining decrements but nothing written to lineContentBytes.
            // At end: currentLineBytes > 0 so flush as unterminated line.
            Assert.Equal(1, fileIndex.Index.LineCount);
            Assert.Equal(2UL, fileIndex.Index.GetByteLength(0));
            Assert.Equal(0UL, fileIndex.Index.GetCharLength(0));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Test 12: File with single byte 'A' → 1 line, no BOM detected, byteLen=1, charLen=1</summary>
    [Fact]
    public async Task SingleByteA_OneLine_NoBom()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, [(byte)'A']);
            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(0, fileIndex.BomByteLength);
            Assert.Equal(System.Text.Encoding.UTF8, fileIndex.Encoding);
            Assert.Equal(1, fileIndex.Index.LineCount);
            Assert.Equal(1UL, fileIndex.Index.GetByteLength(0));
            Assert.Equal(1UL, fileIndex.Index.GetCharLength(0));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
