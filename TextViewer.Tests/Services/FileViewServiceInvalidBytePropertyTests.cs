using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 4: Invalid byte replacement
/// For any byte sequence that contains subsequences invalid for the detected encoding,
/// each invalid subsequence SHALL decode to U+FFFD (replacement character), and each
/// U+FFFD SHALL count as exactly one column position.
///
/// **Validates: Requirements 5.2**
/// </summary>
public class FileViewServiceInvalidBytePropertyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFileWithBytes(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_prop4_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static async Task<FileViewService> CreateServiceAndWaitForScan(string path)
    {
        var logger = NullLogger<FileViewService>.Instance;
        var service = new FileViewService(path, CancellationToken.None, logger);

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.ScanState < ScanState.ScanComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        return service;
    }

    /// <summary>
    /// Known invalid UTF-8 byte sequences that must produce U+FFFD replacement characters.
    /// </summary>
    private static readonly byte[][] InvalidUtf8Sequences = new byte[][]
    {
        new byte[] { 0xFE },           // Invalid byte (never valid in UTF-8)
        new byte[] { 0xFF },           // Invalid byte (never valid in UTF-8)
        new byte[] { 0x80 },           // Continuation byte without start byte
        new byte[] { 0xBF },           // Continuation byte without start byte
        new byte[] { 0xC0, 0xAF },    // Overlong encoding of U+002F
        new byte[] { 0xE0, 0x80, 0xAF }, // Overlong 3-byte sequence
    };

    /// <summary>
    /// Generates a random byte array (10–200 bytes) with at least one injected invalid
    /// UTF-8 sequence, terminated by a newline (0x0A).
    /// </summary>
    private static Arbitrary<byte[]> InvalidUtf8ByteArrayArb()
    {
        var gen =
            from totalLen in Gen.Choose(10, 200)
            from validBytes in Gen.ArrayOf(Gen.Choose(0x20, 0x7E).Select(i => (byte)i), totalLen)
            from invalidIdx in Gen.Choose(0, InvalidUtf8Sequences.Length - 1)
            from insertPos in Gen.Choose(0, Math.Max(0, validBytes.Length - 1))
            select BuildByteArrayWithInvalidSequence(validBytes, InvalidUtf8Sequences[invalidIdx], insertPos);

        return Arb.From(gen);
    }

    private static byte[] BuildByteArrayWithInvalidSequence(byte[] validBytes, byte[] invalidSeq, int insertPos)
    {
        // Insert the invalid sequence at the specified position, then append newline
        var result = new List<byte>();

        // Add valid bytes before insertion point
        for (int i = 0; i < insertPos && i < validBytes.Length; i++)
            result.Add(validBytes[i]);

        // Insert invalid sequence
        result.AddRange(invalidSeq);

        // Add remaining valid bytes after insertion point
        for (int i = insertPos; i < validBytes.Length; i++)
            result.Add(validBytes[i]);

        // Append newline to ensure it's a complete line
        result.Add(0x0A);

        return result.ToArray();
    }

    /// <summary>
    /// For any byte array containing invalid UTF-8 sequences:
    /// - U+FFFD must appear in the decoded result (confirming invalid bytes are replaced)
    /// - Each U+FFFD counts as exactly 1 column position (content length equals
    ///   number of valid chars + number of replacement chars)
    ///
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property InvalidBytes_ProduceReplacementCharacter_EachCountsAsOneColumn()
    {
        return Prop.ForAll(
            InvalidUtf8ByteArrayArb(),
            bytes =>
            {
                var path = CreateTempFileWithBytes(bytes);

                using var service = CreateServiceAndWaitForScan(path).Result;

                // Request the full first line with a large colCount
                var result = service.GetViewAsync(0, 0, 1, 10000).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                var row = result.Value.Rows[0];

                // The row includes the delimiter (\n), so strip it for content analysis
                string content;
                if (row.EndsWith("\n"))
                    content = row.Substring(0, row.Length - 1);
                else
                    content = row;

                // Assert: U+FFFD must be present (since we injected invalid bytes)
                bool hasReplacement = content.Contains('\uFFFD');
                if (!hasReplacement)
                    return false.Label(
                        $"Expected U+FFFD in decoded content but none found. " +
                        $"Content length={content.Length}, bytes length={bytes.Length}");

                // Assert: Each U+FFFD counts as exactly 1 column position.
                // Verify by independently decoding the full byte content and comparing.
                // The content length should equal the number of decoded chars (valid + replacement).
                int replacementCount = content.Count(c => c == '\uFFFD');
                int nonReplacementCount = content.Length - replacementCount;

                // Each replacement char occupies exactly 1 column, so total columns = content.Length
                // This is inherently true if we got here, but let's verify the column position
                // by requesting with a colCount equal to content.Length and confirming we get the same content.
                var verifyResult = service.GetViewAsync(0, 0, 1, content.Length).Result;
                if (!verifyResult.IsSuccess)
                    return false.Label($"Verification request failed: {verifyResult.Error.Message}");

                var verifyRow = verifyResult.Value.Rows[0];
                string verifyContent;
                if (verifyRow.EndsWith("\n"))
                    verifyContent = verifyRow.Substring(0, verifyRow.Length - 1);
                else
                    verifyContent = verifyRow;

                // With colCount = content.Length, we should get exactly the same content
                bool contentMatches = verifyContent == content;
                if (!contentMatches)
                    return false.Label(
                        $"Column counting mismatch: requesting colCount={content.Length} " +
                        $"returned {verifyContent.Length} chars instead of {content.Length}. " +
                        $"Replacements={replacementCount}, NonReplacement={nonReplacementCount}");

                return (hasReplacement && contentMatches)
                    .Label($"OK: {replacementCount} U+FFFD replacements, " +
                           $"content length={content.Length}, each counts as 1 column");
            });
    }
}
