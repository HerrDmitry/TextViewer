using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for Full_Scan char-length correctness.
/// Validates: Requirements 3.2, 3.3, 3.4
/// </summary>
public class FullScanCharLengthPropertyTests
{
    /// <summary>
    /// Represents a generated test case for Full_Scan char-length verification.
    /// </summary>
    private sealed record TestCase(string[] Lines, string[] LineEndings, bool HasBom, byte[]? InvalidByteInsertions);

    /// <summary>
    /// Generates random strings with multi-byte chars (UTF-8 encoding), optional BOM,
    /// and optional invalid byte sequences.
    /// </summary>
    private static Arbitrary<TestCase> FullScanTestCases()
    {
        // Character generators for different UTF-8 byte widths
        var asciiChar = Gen.Choose(0x20, 0x7E).Select(c => (char)c); // printable ASCII (1-byte UTF-8)
        var accentedChar = Gen.Choose(0x00C0, 0x00FF).Select(c => (char)c); // Latin Extended (2-byte UTF-8)
        var cjkChar = Gen.Choose(0x4E00, 0x9FFF).Select(c => (char)c); // CJK Unified (3-byte UTF-8)

        // Emoji/surrogate pairs (4-byte UTF-8) - use specific known emoji code points
        var emojiString = Gen.Elements(
            "\U0001F600", // 😀
            "\U0001F4A9", // 💩
            "\U0001F680", // 🚀
            "\U0001F30D", // 🌍
            "\U0001F525"  // 🔥
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

        // Invalid byte sequences (optional)
        // These are bytes that are invalid in UTF-8
        var invalidByte = Gen.Elements<byte>(
            0xFF, 0xFE, // Not valid UTF-8 start bytes
            0xC0, 0xC1, // Overlong encoding start bytes (invalid)
            0x80, 0x81, 0xBF // Continuation bytes without start byte
        );

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
    /// Property 2: Full_Scan char-length correctness
    ///
    /// For any file content and detected encoding, the Char_Length stored for each line
    /// SHALL equal the .Length of the .NET string produced by decoding that line's content
    /// bytes (excluding delimiter bytes) with the encoding (using DecoderFallback.ReplacementFallback),
    /// excluding any BOM character.
    ///
    /// Validates: Requirements 3.2, 3.3, 3.4
    /// </summary>
    [Property(MaxTest = 10)]
    public Property FullScan_CharLength_Correctness()
    {
        return Prop.ForAll(
            FullScanTestCases(),
            (TestCase testCase) =>
            {
                string tempFile = Path.GetTempFileName();
                try
                {
                    // Build the file bytes
                    byte[] fileBytes = BuildFileBytes(testCase);

                    // Write to temp file
                    File.WriteAllBytes(tempFile, fileBytes);

                    // Run full scan (Quick + Full)
                    var logger = NullLogger<FileIndex>.Instance;
                    using var fileIndex = new FileIndex(tempFile, CancellationToken.None, logger);
                    fileIndex.StartScanAsync().GetAwaiter().GetResult();

                    // Verify state is FullScanComplete
                    if (fileIndex.State != ScanState.FullScanComplete)
                    {
                        return false.Label(
                            $"Expected FullScanComplete, got {fileIndex.State}. Error: {fileIndex.Error}");
                    }

                    var index = fileIndex.Index;
                    int lineCount = index.LineCount;

                    // Compute expected char lengths
                    int[] expectedCharLengths = ComputeExpectedCharLengths(testCase, fileBytes);

                    if (lineCount != expectedCharLengths.Length)
                    {
                        return false.Label(
                            $"Line count mismatch: index has {lineCount}, expected {expectedCharLengths.Length}");
                    }

                    // Verify each line's char length
                    for (int i = 0; i < lineCount; i++)
                    {
                        ulong? storedCharLength = index.GetCharLength(i);
                        if (storedCharLength == null)
                        {
                            return false.Label(
                                $"Line {i}: GetCharLength returned null (Full_Scan should have written all char lengths)");
                        }

                        if ((int)storedCharLength.Value != expectedCharLengths[i])
                        {
                            return false.Label(
                                $"Line {i}: stored Char_Length = {storedCharLength.Value}, expected = {expectedCharLengths[i]}");
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
    /// Builds the raw file bytes from the test case, including optional BOM and invalid bytes.
    /// </summary>
    private static byte[] BuildFileBytes(TestCase testCase)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var ms = new MemoryStream();

        // Optional BOM (EF BB BF)
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

            // Write line ending (except for the last line which may be unterminated)
            // We always write a line ending for all lines to keep it simple
            if (i < testCase.Lines.Length - 1)
            {
                byte[] endingBytes = utf8.GetBytes(testCase.LineEndings[i]);
                ms.Write(endingBytes);
            }
            else
            {
                // Last line: 50% chance of having a line ending (determined by content)
                // For simplicity, always add the ending for the last line too
                byte[] endingBytes = utf8.GetBytes(testCase.LineEndings[i]);
                ms.Write(endingBytes);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Computes the expected char lengths by simulating what the Full_Scan should produce.
    /// This re-parses the file bytes the same way FileIndex does: identify lines by delimiters,
    /// strip BOM from first line, decode with ReplacementFallback, and measure .Length.
    /// </summary>
    private static int[] ComputeExpectedCharLengths(TestCase testCase, byte[] fileBytes)
    {
        // Parse the file bytes into lines (same logic as Quick_Scan)
        var lineByteRanges = ParseLineByteRanges(fileBytes);

        if (lineByteRanges.Count == 0)
            return Array.Empty<int>();

        // Detect BOM
        int bomByteLength = 0;
        if (fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF)
        {
            bomByteLength = 3;
        }

        // Create decoder with replacement fallback
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

            // For first line, exclude BOM
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

            // Decode and get string length
            string decoded = decoderEncoding.GetString(fileBytes, contentStart, contentLength);
            charLengths[i] = decoded.Length;
        }

        return charLengths;
    }

    /// <summary>
    /// Parses file bytes into line byte ranges (start, length) using the same
    /// line-ending detection logic as Quick_Scan.
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
                // LF - end of line (includes the LF byte)
                int lineLength = i - lineStart + 1;
                ranges.Add((lineStart, lineLength));
                lineStart = i + 1;
            }
            else if (b == 0x0D)
            {
                // CR - check if followed by LF (CRLF)
                if (i + 1 < fileBytes.Length && fileBytes[i + 1] == 0x0A)
                {
                    // CRLF
                    int lineLength = i - lineStart + 2;
                    ranges.Add((lineStart, lineLength));
                    lineStart = i + 2;
                    i++; // skip the LF
                }
                else
                {
                    // Standalone CR
                    int lineLength = i - lineStart + 1;
                    ranges.Add((lineStart, lineLength));
                    lineStart = i + 1;
                }
            }
        }

        // Handle final unterminated line
        if (lineStart < fileBytes.Length)
        {
            ranges.Add((lineStart, fileBytes.Length - lineStart));
        }

        return ranges;
    }

    /// <summary>
    /// Determines the number of delimiter bytes at the end of a line's byte range.
    /// </summary>
    private static int GetDelimiterByteCount(byte[] fileBytes, int start, int length)
    {
        if (length == 0)
            return 0;

        int end = start + length;

        // Check for CRLF
        if (length >= 2 && fileBytes[end - 2] == 0x0D && fileBytes[end - 1] == 0x0A)
            return 2;

        // Check for LF
        if (fileBytes[end - 1] == 0x0A)
            return 1;

        // Check for CR
        if (fileBytes[end - 1] == 0x0D)
            return 1;

        return 0;
    }
}
