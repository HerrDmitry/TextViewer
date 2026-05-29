using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for Quick_Scan byte-length round-trip.
/// Validates: Requirements 2.2, 2.3, 2.4
/// </summary>
public class QuickScanRoundTripPropertyTests
{
    /// <summary>
    /// Generates random byte arrays (0–10KB) with mixed line endings (LF/CR/CRLF)
    /// interspersed with random content bytes (non-CR, non-LF).
    /// </summary>
    private static Arbitrary<byte[]> MixedLineEndingByteArrays()
    {
        // Content bytes: any byte except CR (0x0D) and LF (0x0A)
        var contentByte = Gen.OneOf(
            Gen.Choose(0x00, 0x09).Select(v => (byte)v),
            Gen.Choose(0x0B, 0x0C).Select(v => (byte)v),
            Gen.Choose(0x0E, 0xFF).Select(v => (byte)v)
        );

        // Line ending sequences
        var lf = Gen.Constant(new byte[] { 0x0A });
        var cr = Gen.Constant(new byte[] { 0x0D });
        var crlf = Gen.Constant(new byte[] { 0x0D, 0x0A });
        var lineEnding = Gen.OneOf(lf, cr, crlf);

        // A content chunk: 0–200 random content bytes
        var contentChunk = Gen.Choose(0, 200)
            .SelectMany(len => Gen.ArrayOf(contentByte, len));

        // A single "line segment" = content bytes followed by a line ending
        var lineSegment = contentChunk.SelectMany(content =>
            lineEnding.Select(ending =>
            {
                var result = new byte[content.Length + ending.Length];
                content.CopyTo(result, 0);
                ending.CopyTo(result, content.Length);
                return result;
            }));

        // Build the file: N line segments + optional trailing content (unterminated last line)
        var gen = Gen.Choose(0, 50).SelectMany(lineCount =>
        {
            var lines = Gen.ArrayOf(lineSegment, lineCount);

            // Optionally add trailing content (unterminated last line)
            var trailingContent = Gen.OneOf(
                Gen.Constant(Array.Empty<byte>()),
                Gen.Choose(1, 100).SelectMany(len => Gen.ArrayOf(contentByte, len))
            );

            return lines.SelectMany(linesArr =>
                trailingContent.Select(trailing =>
                {
                    var totalLen = linesArr.Sum(a => a.Length) + trailing.Length;
                    var result = new byte[totalLen];
                    int pos = 0;
                    foreach (var chunk in linesArr)
                    {
                        chunk.CopyTo(result, pos);
                        pos += chunk.Length;
                    }
                    trailing.CopyTo(result, pos);
                    return result;
                }));
        });

        // Cap at 10KB
        var capped = gen.Select(arr => arr.Length > 10240 ? arr[..10240] : arr);

        return Arb.From(capped);
    }

    /// <summary>
    /// Property 1: Quick_Scan byte-length round-trip
    ///
    /// For any byte sequence representing file content:
    /// - The sum of all stored Byte_Lengths SHALL equal the total file size in bytes
    /// - Reconstructing the file by concatenating each line's bytes SHALL produce the original byte sequence
    /// - GetByteOffset(i) == sum of Byte_Lengths[0..i-1] for all i
    /// - GetByteOffset(LineCount) == file size
    ///
    /// Validates: Requirements 2.2, 2.3, 2.4
    /// </summary>
    [Property(MaxTest = 10)]
    public Property QuickScan_ByteLength_RoundTrip()
    {
        return Prop.ForAll(
            MixedLineEndingByteArrays(),
            (byte[] fileContent) =>
            {
                string tempFile = Path.GetTempFileName();
                try
                {
                    // Write content to temp file
                    File.WriteAllBytes(tempFile, fileContent);
                    long fileSize = new FileInfo(tempFile).Length;

                    // Run Quick_Scan
                    var logger = NullLogger<FileIndex>.Instance;
                    using var fileIndex = new FileIndex(tempFile, CancellationToken.None, logger);
                    fileIndex.StartScanAsync().GetAwaiter().GetResult();

                    // Verify state
                    if (fileIndex.State != ScanState.QuickScanComplete &&
                        fileIndex.State != ScanState.FullScanInProgress &&
                        fileIndex.State != ScanState.FullScanComplete)
                    {
                        return false.Label(
                            $"Expected QuickScanComplete (or later), got {fileIndex.State}. Error: {fileIndex.Error}");
                    }

                    var index = fileIndex.Index;
                    int lineCount = index.LineCount;

                    // Empty file → 0 lines
                    if (fileContent.Length == 0)
                    {
                        return (lineCount == 0).Label(
                            $"Empty file should have 0 lines, got {lineCount}");
                    }

                    // Assert 1: sum of all Byte_Lengths == file size
                    ulong sumByteLengths = 0;
                    for (int i = 0; i < lineCount; i++)
                    {
                        sumByteLengths += index.GetByteLength(i);
                    }

                    if (sumByteLengths != (ulong)fileSize)
                    {
                        return false.Label(
                            $"Sum of Byte_Lengths ({sumByteLengths}) != file size ({fileSize})");
                    }

                    // Assert 2: reconstructing file by concatenating each line's bytes
                    // produces original content
                    byte[] reconstructed = new byte[fileSize];
                    int pos = 0;
                    for (int i = 0; i < lineCount; i++)
                    {
                        int len = (int)index.GetByteLength(i);
                        int offset = (int)index.GetByteOffset(i);
                        Array.Copy(fileContent, offset, reconstructed, pos, len);
                        pos += len;
                    }

                    if (!reconstructed.SequenceEqual(fileContent))
                    {
                        return false.Label(
                            "Reconstructed file content does not match original");
                    }

                    // Assert 3: GetByteOffset(i) == sum of Byte_Lengths[0..i-1] for all i
                    ulong expectedOffset = 0;
                    for (int i = 0; i <= lineCount; i++)
                    {
                        ulong actualOffset = index.GetByteOffset(i);
                        if (actualOffset != expectedOffset)
                        {
                            return false.Label(
                                $"GetByteOffset({i}) = {actualOffset}, expected {expectedOffset}");
                        }

                        if (i < lineCount)
                        {
                            expectedOffset += index.GetByteLength(i);
                        }
                    }

                    // Assert 4: GetByteOffset(LineCount) == file size
                    ulong offsetAtEnd = index.GetByteOffset(lineCount);
                    if (offsetAtEnd != (ulong)fileSize)
                    {
                        return false.Label(
                            $"GetByteOffset(LineCount={lineCount}) = {offsetAtEnd}, expected file size {fileSize}");
                    }

                    return true.Label("All round-trip assertions passed");
                }
                finally
                {
                    try { File.Delete(tempFile); }
                    catch { /* cleanup best-effort */ }
                }
            });
    }
}
