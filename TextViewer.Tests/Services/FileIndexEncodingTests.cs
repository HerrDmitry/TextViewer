using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Unit tests for FileIndex Encoding and BomByteLength properties.
/// Validates: Requirements 3.1, 3.2, 3.3
/// </summary>
public class FileIndexEncodingTests
{
    private readonly NullLogger<FileIndex> _logger = NullLogger<FileIndex>.Instance;

    // --- Requirement 3.1, 3.2: UTF-8 BOM detection sets Encoding and BomByteLength=3 ---

    [Fact]
    public async Task Utf8Bom_SetsEncodingAndBomByteLength3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // UTF-8 BOM: 0xEF 0xBB 0xBF followed by content
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var content = "hello\n"u8.ToArray();
            File.WriteAllBytes(tempFile, bom.Concat(content).ToArray());

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(Encoding.UTF8, fileIndex.Encoding);
            Assert.Equal(3, fileIndex.BomByteLength);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 3.1, 3.2: UTF-16 LE BOM sets Encoding and BomByteLength=2 ---

    [Fact]
    public async Task Utf16LeBom_SetsEncodingAndBomByteLength2()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // UTF-16 LE BOM: 0xFF 0xFE followed by UTF-16 LE content
            var bom = new byte[] { 0xFF, 0xFE };
            var content = Encoding.Unicode.GetBytes("hello\n");
            File.WriteAllBytes(tempFile, bom.Concat(content).ToArray());

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(Encoding.Unicode, fileIndex.Encoding);
            Assert.Equal(2, fileIndex.BomByteLength);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 3.1, 3.2: UTF-16 BE BOM sets Encoding and BomByteLength=2 ---

    [Fact]
    public async Task Utf16BeBom_SetsEncodingAndBomByteLength2()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // UTF-16 BE BOM: 0xFE 0xFF followed by UTF-16 BE content
            var bom = new byte[] { 0xFE, 0xFF };
            var content = Encoding.BigEndianUnicode.GetBytes("hello\n");
            File.WriteAllBytes(tempFile, bom.Concat(content).ToArray());

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(Encoding.BigEndianUnicode, fileIndex.Encoding);
            Assert.Equal(2, fileIndex.BomByteLength);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 3.1, 3.2: UTF-32 LE BOM sets Encoding and BomByteLength=4 ---

    [Fact]
    public async Task Utf32LeBom_SetsEncodingAndBomByteLength4()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // UTF-32 LE BOM: 0xFF 0xFE 0x00 0x00 followed by UTF-32 LE content
            var bom = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
            var content = Encoding.UTF32.GetBytes("hello\n");
            File.WriteAllBytes(tempFile, bom.Concat(content).ToArray());

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(Encoding.UTF32, fileIndex.Encoding);
            Assert.Equal(4, fileIndex.BomByteLength);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 3.1, 3.2: UTF-32 BE BOM sets Encoding and BomByteLength=4 ---

    [Fact]
    public async Task Utf32BeBom_SetsEncodingAndBomByteLength4()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // UTF-32 BE BOM: 0x00 0x00 0xFE 0xFF followed by UTF-32 BE content
            var bom = new byte[] { 0x00, 0x00, 0xFE, 0xFF };
            var utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: false);
            var content = utf32Be.GetBytes("hello\n");
            File.WriteAllBytes(tempFile, bom.Concat(content).ToArray());

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            // UTF-32 BE encoding
            Assert.Equal("utf-32BE", fileIndex.Encoding.WebName);
            Assert.Equal(4, fileIndex.BomByteLength);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 3.1, 3.2: No BOM defaults to UTF-8 and BomByteLength=0 ---

    [Fact]
    public async Task NoBom_DefaultsToUtf8AndBomByteLength0()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Plain ASCII/UTF-8 content without BOM
            File.WriteAllBytes(tempFile, "hello\nworld\n"u8.ToArray());

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            await fileIndex.StartScanAsync();

            Assert.Equal(Encoding.UTF8, fileIndex.Encoding);
            Assert.Equal(0, fileIndex.BomByteLength);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Requirement 3.3: Encoding and BomByteLength available after scan starts ---

    [Fact]
    public async Task EncodingProperties_AvailableAfterScanCompletes()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var bom = new byte[] { 0xFF, 0xFE };
            var content = Encoding.Unicode.GetBytes("test\n");
            File.WriteAllBytes(tempFile, bom.Concat(content).ToArray());

            using var cts = new CancellationTokenSource();
            using var fileIndex = new FileIndex(tempFile, cts.Token, _logger);

            // Before scan, defaults should be set
            Assert.Equal(Encoding.UTF8, fileIndex.Encoding);
            Assert.Equal(0, fileIndex.BomByteLength);

            await fileIndex.StartScanAsync();

            // After scan, detected encoding should be set
            Assert.Equal(Encoding.Unicode, fileIndex.Encoding);
            Assert.Equal(2, fileIndex.BomByteLength);
            Assert.NotNull(fileIndex.Encoding);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
