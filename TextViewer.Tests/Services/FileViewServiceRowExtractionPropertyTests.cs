using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 1: Row extraction correctness
/// For any file content (with any encoding and line endings) and any valid view request parameters
/// (startLine, startCol, rowCount, colCount), each row in the result SHALL equal the substring of
/// the decoded line starting at startCol with length up to colCount, followed by the line's
/// original delimiter — matching the result of independently decoding the full file and slicing
/// the same region.
///
/// **Validates: Requirements 1.2, 5.1, 5.5**
/// </summary>
public class FileViewServiceRowExtractionPropertyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>
    /// Parameters for the property test: file content bytes and view request params.
    /// </summary>
    private record TestInput(
        byte[] FileContent,
        int StartLine,
        int StartCol,
        int RowCount,
        int ColCount);

    /// <summary>
    /// Generates random UTF-8 content (0–4KB) with mixed line endings (LF, CRLF, CR).
    /// Then generates random valid view request parameters.
    /// </summary>
    private static Arbitrary<TestInput> RowExtractionArb()
    {
        var contentByte = Gen.Choose(0x20, 0x7E).Select(i => (byte)i);

        // Line ending sequences
        var lf = Gen.Constant(new byte[] { 0x0A });
        var cr = Gen.Constant(new byte[] { 0x0D });
        var crlf = Gen.Constant(new byte[] { 0x0D, 0x0A });
        var lineEnding = Gen.OneOf(lf, cr, crlf);

        // A content chunk: 0–100 random content bytes
        var contentChunk = Gen.Choose(0, 100)
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
        var fileGen = Gen.Choose(0, 30).SelectMany(lineCount =>
        {
            var lines = Gen.ArrayOf(lineSegment, lineCount);

            // Optionally add trailing content (unterminated last line)
            var trailingContent = Gen.OneOf(
                Gen.Constant(Array.Empty<byte>()),
                Gen.Choose(1, 50).SelectMany(len => Gen.ArrayOf(contentByte, len))
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

        // Cap at 4KB
        var cappedFileGen = fileGen.Select(arr => arr.Length > 4096 ? arr[..4096] : arr);

        // Generate view request params based on the file content
        var gen = cappedFileGen.SelectMany(fileContent =>
        {
            int totalLines = CountLines(fileContent);
            int maxStartLine = Math.Max(0, totalLines - 1);

            return Gen.Choose(0, maxStartLine).SelectMany(startLine =>
                Gen.Choose(0, 20).SelectMany(startCol =>
                    Gen.Choose(1, 10).SelectMany(rowCount =>
                        Gen.Choose(1, 80).Select(colCount =>
                            new TestInput(fileContent, startLine, startCol, rowCount, colCount)))));
        });

        return Arb.From(gen);
    }

    /// <summary>
    /// Counts lines in byte content using the same logic as FileIndex (LF, CRLF, CR delimiters).
    /// </summary>
    private static int CountLines(byte[] content)
    {
        if (content.Length == 0) return 0;

        int lines = 1; // At least one line if there's any content
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == 0x0A)
                lines++;
            else if (content[i] == 0x0D)
            {
                if (i + 1 < content.Length && content[i + 1] == 0x0A)
                    i++; // CRLF counts as one delimiter
                lines++;
            }
        }
        return lines;
    }

    /// <summary>
    /// Independently splits file content into lines preserving delimiters.
    /// Returns a list of (content, delimiter) tuples.
    /// </summary>
    private static List<(string content, string delimiter)> SplitIntoLinesWithDelimiters(byte[] fileBytes)
    {
        var result = new List<(string content, string delimiter)>();
        var text = Encoding.UTF8.GetString(fileBytes);

        int start = 0;
        while (start <= text.Length)
        {
            if (start == text.Length)
            {
                // We've consumed all text; only add empty line if last char was a delimiter
                break;
            }

            int pos = start;
            // Find next line ending
            while (pos < text.Length && text[pos] != '\r' && text[pos] != '\n')
                pos++;

            string content = text.Substring(start, pos - start);
            string delimiter;

            if (pos >= text.Length)
            {
                // No delimiter at end (last unterminated line)
                delimiter = "";
                result.Add((content, delimiter));
                break;
            }
            else if (text[pos] == '\r' && pos + 1 < text.Length && text[pos + 1] == '\n')
            {
                delimiter = "\r\n";
                result.Add((content, delimiter));
                start = pos + 2;
            }
            else if (text[pos] == '\n')
            {
                delimiter = "\n";
                result.Add((content, delimiter));
                start = pos + 1;
            }
            else // '\r' alone
            {
                delimiter = "\r";
                result.Add((content, delimiter));
                start = pos + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Computes the expected row string for a given line by slicing at startCol/colCount
    /// and appending the delimiter.
    /// </summary>
    private static string ComputeExpectedRow(string content, string delimiter, int startCol, int colCount)
    {
        if (startCol >= content.Length)
            return delimiter;

        int end = Math.Min(startCol + colCount, content.Length);
        return content.Substring(startCol, end - startCol) + delimiter;
    }

    private string CreateTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_prop1_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static async Task<FileViewService> CreateServiceAndWaitForScan(string path)
    {
        var logger = NullLogger<FileViewService>.Instance;
        var service = new FileViewService(path, CancellationToken.None, logger);

        // Wait for scan to complete (ScanComplete or beyond)
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.ScanState < ScanState.ScanComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        return service;
    }

    /// <summary>
    /// For any file content (UTF-8, no BOM) with mixed line endings and any valid view request,
    /// each row in the result SHALL equal the substring of the decoded line starting at startCol
    /// with length up to colCount, followed by the line's original delimiter.
    ///
    /// **Validates: Requirements 1.2, 5.1, 5.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property RowExtraction_MatchesIndependentDecodeAndSlice()
    {
        return Prop.ForAll(
            RowExtractionArb(),
            input =>
            {
                // Write content to temp file
                var path = CreateTempFile(input.FileContent);

                // Create service and wait for scan
                using var service = CreateServiceAndWaitForScan(path).Result;

                // Edge case: empty file
                if (input.FileContent.Length == 0)
                {
                    var emptyResult = service.GetViewAsync(
                        input.StartLine, input.StartCol, input.RowCount, input.ColCount).Result;

                    if (!emptyResult.IsSuccess)
                        return false.Label($"Expected success for empty file but got error: {emptyResult.Error.Message}");

                    return (emptyResult.Value.Rows.Count == 1 && emptyResult.Value.Rows[0] == "")
                        .Label("Empty file should return single empty string");
                }

                // Get result from FileViewService
                var result = service.GetViewAsync(
                    input.StartLine, input.StartCol, input.RowCount, input.ColCount).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                // Independently decode and split the file
                var lines = SplitIntoLinesWithDelimiters(input.FileContent);
                int totalLines = lines.Count;

                // If startLine >= totalLines, expect single empty string
                if (input.StartLine >= totalLines)
                {
                    return (result.Value.Rows.Count == 1 && result.Value.Rows[0] == "")
                        .Label($"startLine={input.StartLine} >= totalLines={totalLines}: expected single empty string");
                }

                // Compute expected rows
                int expectedRowCount = Math.Min(input.RowCount, totalLines - input.StartLine);
                var expectedRows = new List<string>();
                for (int i = 0; i < expectedRowCount; i++)
                {
                    int lineIdx = input.StartLine + i;
                    var (content, delimiter) = lines[lineIdx];
                    expectedRows.Add(ComputeExpectedRow(content, delimiter, input.StartCol, input.ColCount));
                }

                // Assert row count matches
                if (result.Value.Rows.Count != expectedRows.Count)
                    return false.Label(
                        $"Row count mismatch: expected {expectedRows.Count}, got {result.Value.Rows.Count}");

                // Assert each row matches
                for (int i = 0; i < expectedRows.Count; i++)
                {
                    if (result.Value.Rows[i] != expectedRows[i])
                        return false.Label(
                            $"Row {i} mismatch at startLine={input.StartLine}, startCol={input.StartCol}, " +
                            $"colCount={input.ColCount}: expected [{Escape(expectedRows[i])}], " +
                            $"got [{Escape(result.Value.Rows[i])}]");
                }

                return true.Label("All rows match independent decode + slice");
            });
    }

    private static string Escape(string s)
    {
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
