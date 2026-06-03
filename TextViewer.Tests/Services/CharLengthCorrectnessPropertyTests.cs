using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Feature: unified-scan-pass, Property 2: Char-length correctness
/// Validates: Requirements 3.1, 3.2
/// </summary>
public class CharLengthCorrectnessPropertyTests
{
    /// <summary>
    /// Represents a generated test case with lines, BOM presence, and optional invalid bytes.
    /// </summary>
    private sealed record TestCase(string[] Lines, string[] LineEndings, bool HasBom, byte[]? InvalidByteInsertions);

    /// <summary>
    /// Generates random strings with multi-byte chars (UTF-8), optional BOM,
    /// and optional invalid byte sequences for char-length verification.
    /// </summary>
    private static Arbitrary<TestCase> CharLengthTestCases()
    {
        // Character generators for different UTF-8 byte widths
        var asciiChar = Gen.Choose(0x20, 0x7E).Select(c => (char)c);
        var accentedChar = Gen.Choose(0x00C0, 0x00FF).Select(c => (char)c);
        var cjkChar = Gen.Choose(0x4E00, 0x9FFF).Select(c => (char)c);

        // Emoji/surrogate pairs (4-byte UTF-8)
        var emojiString = Gen.Elements(
            "\U0001F600", "\U0001F4A9", "\U0001F680", "\U0001F30D", "\U0001F525"
        );

        // A single "character unit" - either a single char or a surrogate pair string
        var charUnit = Gen.OneOf(
            asciiChar.Select(c => c.ToString()),
            accentedChar.Select(c => c.ToString()),
            cjkChar.Select(c => c.ToString()),
            emojiString
        );

        // Generate a line content string (0-50 character units)
        var lineContent = Gen.Choose(0, 50)
            .SelectMany(len => Gen.ArrayOf(charUnit, len))
            .Select(units => string.Concat(units));

        // Line endings
        var lineEnding = Gen.Elements("\n", "\r", "\r\n");

        // Number of lines (1-20)
        var lineCount = Gen.Choose(1, 20);

        // BOM flag
        var hasBom = Gen.Elements(true, false);

        // Invalid byte sequences (optional) - bytes invalid in UTF-8
        var invalidByte = Gen.Elements<byte>(0xFF, 0xFE, 0xC0, 0xC1, 0x80, 0x81, 0xBF);

        var invalidBytes = Gen.OneOf(
            Gen.Constant((byte[]?)null),
            Gen.Choose(1, 5)
                .SelectMany(len => Gen.ArrayOf(invalidByte, len))
                .Select(arr => (byte[]?)arr)
        );

        var gen = lineCount.SelectMany(count =>
            Gen.ArrayOf(lineContent, count).SelectMany(lines =>
                Gen.ArrayOf(lineEnding, count).SelectMany(endings =>
                    hasBom.SelectMany(bom =>
                        invalidBytes.Select(inv =>
                            new TestCase(lines, endings, bom, inv))))));

        return Arb.From(gen);
    }

    /// <summary>
    /// **Validates: Requirements 3.1, 3.2**
    ///
    /// For any file content and detected encoding (UTF-8, with and without BOM),
    /// the Char_Length stored for each line SHALL equal the .Length of the .NET string
    /// produced by decoding that line's content bytes (excluding delimiter bytes) with the
    /// encoding using ReplacementFallback, excluding BOM characters on the first line only.
    /// </summary>
    [Property(MaxTest = 10)]
    public Property CharLength_Correctness()
    {
        return Prop.ForAll(
            CharLengthTestCases(),
            (TestCase testCase) =>
            {
                string tempFile = Path.GetTempFileName();
                try
                {
                    // Build file bytes
                    byte[] fileBytes = BuildFileBytes(testCase);
                    File.WriteAllBytes(tempFile, fileBytes);

                    // Run unified scan
                    var logger = NullLogger<FileIndex>.Instance;
                    using var fileIndex = new FileIndex(tempFile, CancellationToken.None, logger);
                    fileIndex.StartScanAsync().GetAwaiter().GetResult();

                    // Verify state is ScanComplete
                    if (fileIndex.State != ScanState.ScanComplete)
                    {
                        return false.Label(
                            $"Expected ScanComplete, got {fileIndex.State}. Error: {fileIndex.Error}");
                    }

                    var index = fileIndex.Index;
                    int lineCount = index.LineCount;

                    // Compute expected char lengths independently
                    int[] expectedCharLengths = ComputeExpectedCharLengths(fileBytes);

                    if (lineCount != expectedCharLengths.Length)
                    {
                        return false.Label(
                            $"Line count mismatch: index has {lineCount}, expected {expectedCharLengths.Length}");
                    }

                    // Verify each line's char length
                    for (int i = 0; i < lineCount; i++)
                    {
                        ulong storedCharLength = index.GetCharLength(i);

                        if ((int)storedCharLength != expectedCharLengths[i])
                        {
                            return false.Label(
                                $"Line {i}: stored Char_Length = {storedCharLength}, expected = {expectedCharLengths[i]}");
                        }
                    }

                    return true.Label("All char-length assertions passed");
                }
                finally
                {
                    try { File.Delete(tempFile); }
                    catch { /* cleanup best-effort */ }
                }
            });
    }

    /// <summary>
    /// Builds raw file bytes from the test case, including optional BOM and invalid bytes.
    /// </summary>
    private static byte[] BuildFileBytes(TestCase testCase)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var ms = new MemoryStream();

        // Optional UTF-8 BOM (EF BB BF)
        if (testCase.HasBom)
        {
            ms.Write(new byte[] { 0xEF, 0xBB, 0xBF });
        }

        for (int i = 0; i < testCase.Lines.Length; i++)
        {
            // Encode line content as UTF-8
            byte[] contentBytes = utf8.GetBytes(testCase.Lines[i]);

            // Optionally insert invalid bytes into the first line's content
            if (i == 0 && testCase.InvalidByteInsertions != null && testCase.InvalidByteInsertions.Length > 0)
            {
                ms.Write(contentBytes);
                ms.Write(testCase.InvalidByteInsertions);
            }
            else
            {
                ms.Write(contentBytes);
            }

            // Write line ending for all lines
            byte[] endingBytes = utf8.GetBytes(testCase.LineEndings[i]);
            ms.Write(endingBytes);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Computes expected char lengths by independently parsing file bytes:
    /// identify lines by delimiters, strip BOM from first line, decode with
    /// ReplacementFallback, and measure .Length.
    /// </summary>
    private static int[] ComputeExpectedCharLengths(byte[] fileBytes)
    {
        var lineByteRanges = ParseLineByteRanges(fileBytes);

        if (lineByteRanges.Count == 0)
            return Array.Empty<int>();

        // Detect BOM
        int bomByteLength = 0;
        if (fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF)
            bomByteLength = 3;

        // Create decoder encoding with replacement fallback
        Encoding decoderEncoding = Encoding.GetEncoding(
            Encoding.UTF8.CodePage,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);

        var charLengths = new int[lineByteRanges.Count];

        for (int i = 0; i < lineByteRanges.Count; i++)
        {
            var (start, length) = lineByteRanges[i];

            // Determine delimiter bytes at end
            int delimiterBytes = GetDelimiterByteCount(fileBytes, start, length);
            int contentStart = start;
            int contentLength = length - delimiterBytes;

            // For first line, exclude BOM bytes from content
            if (i == 0 && bomByteLength > 0 && contentLength >= bomByteLength)
            {
                contentStart += bomByteLength;
                contentLength -= bomByteLength;
            }

            if (contentLength <= 0)
            {
                charLengths[i] = 0;
                continue;
            }

            // Decode and get .NET string length
            string decoded = decoderEncoding.GetString(fileBytes, contentStart, contentLength);
            charLengths[i] = decoded.Length;
        }

        return charLengths;
    }

    /// <summary>
    /// Parses file bytes into line byte ranges (start, length) using
    /// line-ending detection (LF, CR, CRLF).
    /// </summary>
    private static List<(int Start, int Length)> ParseLineByteRanges(byte[] fileBytes)
    {
        var ranges = new List<(int Start, int Length)>();
        if (fileBytes.Length == 0)
            return ranges;

        int lineStart = 0;

        for (int i = 0; i < fileBytes.Length; i++)
        {
            byte b = fileBytes[i];

            if (b == 0x0A)
            {
                int lineLength = i - lineStart + 1;
                ranges.Add((lineStart, lineLength));
                lineStart = i + 1;
            }
            else if (b == 0x0D)
            {
                if (i + 1 < fileBytes.Length && fileBytes[i + 1] == 0x0A)
                {
                    int lineLength = i - lineStart + 2;
                    ranges.Add((lineStart, lineLength));
                    lineStart = i + 2;
                    i++;
                }
                else
                {
                    int lineLength = i - lineStart + 1;
                    ranges.Add((lineStart, lineLength));
                    lineStart = i + 1;
                }
            }
        }

        // Final unterminated line
        if (lineStart < fileBytes.Length)
        {
            ranges.Add((lineStart, fileBytes.Length - lineStart));
        }

        return ranges;
    }

    /// <summary>
    /// Determines delimiter byte count at end of a line's byte range.
    /// </summary>
    private static int GetDelimiterByteCount(byte[] fileBytes, int start, int length)
    {
        if (length == 0)
            return 0;

        int end = start + length;

        if (length >= 2 && fileBytes[end - 2] == 0x0D && fileBytes[end - 1] == 0x0A)
            return 2;

        if (fileBytes[end - 1] == 0x0A)
            return 1;

        if (fileBytes[end - 1] == 0x0D)
            return 1;

        return 0;
    }
}
