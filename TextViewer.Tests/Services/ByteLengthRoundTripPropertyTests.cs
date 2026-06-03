using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Feature: unified-scan-pass, Property 1: Byte-length round-trip
/// Validates: Requirements 1.1, 2.1, 2.2, 2.3
/// </summary>
public class ByteLengthRoundTripPropertyTests
{
    /// <summary>
    /// Generates byte arrays (0–10KB) with a random mix of LF, CR, CRLF line endings,
    /// content bytes, and optionally unterminated final lines.
    /// </summary>
    private static Arbitrary<byte[]> FileContentArbitrary()
    {
        // Generate content segments interleaved with random line endings
        var contentByte = Gen.Choose(0x20, 0x7E).Select(b => (byte)b); // printable ASCII
        var lf = Gen.Constant(new byte[] { 0x0A });
        var cr = Gen.Constant(new byte[] { 0x0D });
        var crlf = Gen.Constant(new byte[] { 0x0D, 0x0A });
        var ending = Gen.OneOf(lf, cr, crlf);

        // A line: 0..200 content bytes followed optionally by an ending
        var lineContent = Gen.Choose(0, 200)
            .SelectMany(len => Gen.ArrayOf(contentByte, len));

        var lineWithEnding = lineContent.SelectMany(content =>
            ending.Select(end => content.Concat(end).ToArray()));

        var lineWithoutEnding = lineContent;

        // File: 0..50 lines-with-endings, then optionally a final unterminated line
        var gen = Gen.Choose(0, 50).SelectMany(lineCount =>
            Gen.Choose(0, 1).SelectMany(hasTrailing =>
            {
                if (lineCount == 0 && hasTrailing == 0)
                    return Gen.Constant(Array.Empty<byte>());

                var terminatedLines = Gen.ArrayOf(lineWithEnding, lineCount)
                    .Select(arrays => arrays.SelectMany(a => a).ToArray());

                if (hasTrailing == 0)
                    return terminatedLines;

                return terminatedLines.SelectMany(terminated =>
                    lineWithoutEnding.Select(trailing =>
                        terminated.Concat(trailing).ToArray()));
            }));

        return Arb.From(gen);
    }

    /// <summary>
    /// Counts expected line count from raw bytes:
    /// - Number of delimiters (LF, CR, CRLF each count as one) plus one if trailing content exists
    /// - Empty file → 0 lines
    /// </summary>
    private static int ExpectedLineCount(byte[] content)
    {
        if (content.Length == 0)
            return 0;

        int delimiters = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == 0x0A)
            {
                delimiters++;
            }
            else if (content[i] == 0x0D)
            {
                delimiters++;
                // If CRLF, skip the LF
                if (i + 1 < content.Length && content[i + 1] == 0x0A)
                    i++;
            }
        }

        // Check if file ends with a delimiter
        bool endsWithDelimiter = false;
        if (content.Length > 0)
        {
            byte last = content[^1];
            if (last == 0x0A || last == 0x0D)
                endsWithDelimiter = true;
        }

        // Line count = delimiters + 1 if trailing content, else just delimiters
        return endsWithDelimiter ? delimiters : delimiters + 1;
    }

    /// <summary>
    /// Property 1: Byte-length round-trip
    ///
    /// For any byte sequence representing file content (with any mix of LF, CR, CRLF,
    /// and unterminated final lines):
    /// - The sum of all stored Byte_Lengths SHALL equal the total file size in bytes
    /// - Reconstructing the file by concatenating each line's bytes (sized by Byte_Length)
    ///   SHALL produce the original byte sequence
    /// - The line count SHALL equal the number of delimiters plus one if trailing content
    ///   exists (zero for empty files)
    ///
    /// **Validates: Requirements 1.1, 2.1, 2.2, 2.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ByteLengths_SumToFileSize_AndReconstruct_AndLineCountCorrect()
    {
        return Prop.ForAll(
            FileContentArbitrary(),
            (byte[] content) =>
            {
                // Write content to temp file
                var tempFile = Path.GetTempFileName();
                try
                {
                    File.WriteAllBytes(tempFile, content);

                    using var cts = new CancellationTokenSource();
                    var logger = NullLogger<FileIndex>.Instance;
                    using var fileIndex = new FileIndex(tempFile, cts.Token, logger);

                    var result = fileIndex.StartScanAsync().GetAwaiter().GetResult();

                    if (!result.IsSuccess)
                        return false.Label($"Scan failed: {result.Error.Message}");

                    var index = fileIndex.Index;
                    int lineCount = index.LineCount;

                    // Property A: Sum of Byte_Lengths == file size
                    ulong sumBytes = 0;
                    for (int i = 0; i < lineCount; i++)
                        sumBytes += index.GetByteLength(i);

                    if (sumBytes != (ulong)content.Length)
                        return false.Label(
                            $"Sum mismatch: sum={sumBytes}, fileSize={content.Length}");

                    // Property B: Reconstruct original bytes via GetByteOffset
                    var reconstructed = new byte[content.Length];
                    ulong offset = 0;
                    for (int i = 0; i < lineCount; i++)
                    {
                        ulong len = index.GetByteLength(i);
                        // Verify offset matches GetByteOffset
                        ulong expectedOffset = index.GetByteOffset(i);
                        if (offset != expectedOffset)
                            return false.Label(
                                $"Offset mismatch at line {i}: computed={offset}, GetByteOffset={expectedOffset}");

                        Array.Copy(content, (long)offset, reconstructed, (long)offset, (long)len);
                        offset += len;
                    }

                    if (!content.SequenceEqual(reconstructed))
                        return false.Label("Reconstruction does not match original bytes");

                    // Property C: Line count == delimiters + 1 (if trailing) or 0 (empty)
                    int expected = ExpectedLineCount(content);
                    if (lineCount != expected)
                        return false.Label(
                            $"LineCount mismatch: got={lineCount}, expected={expected}");

                    return true.Label("Byte-length round-trip holds");
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });
    }
}
