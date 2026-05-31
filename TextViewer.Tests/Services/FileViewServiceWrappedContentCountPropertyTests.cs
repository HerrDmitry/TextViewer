using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 6: Backend wrapped extraction content-count invariant
/// For any generated file content and valid wrapped-mode request parameters,
/// the response contains at most characterCount content characters (characters
/// that are NOT newline delimiters \n, \r\n, \r), and delimiters are present
/// at correct positions (between logical lines).
///
/// **Validates: Requirements 6.1, 6.2, 6.3, 6.4, 6.5, 6.6**
/// </summary>
public class FileViewServiceWrappedContentCountPropertyTests : IDisposable
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
    /// Parameters for the property test: file content and wrapped view request params.
    /// </summary>
    private record TestInput(
        byte[] FileContent,
        int StartLine,
        int CharacterOffset,
        int CharacterCount);

    /// <summary>
    /// Generates random UTF-8 content (0–4KB) with mixed line endings (LF, CRLF, CR),
    /// then generates valid wrapped-mode request parameters.
    /// </summary>
    private static Arbitrary<TestInput> WrappedContentCountArb()
    {
        var contentByte = Gen.Choose(0x20, 0x7E).Select(i => (byte)i);

        // Line ending sequences
        var lf = Gen.Constant(new byte[] { 0x0A });
        var cr = Gen.Constant(new byte[] { 0x0D });
        var crlf = Gen.Constant(new byte[] { 0x0D, 0x0A });
        var lineEnding = Gen.OneOf(lf, cr, crlf);

        // A content chunk: 0–80 random content bytes
        var contentChunk = Gen.Choose(0, 80)
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
        var fileGen = Gen.Choose(1, 20).SelectMany(lineCount =>
        {
            var lines = Gen.ArrayOf(lineSegment, lineCount);

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

        // Generate wrapped view request params based on the file content
        var gen = cappedFileGen.SelectMany(fileContent =>
        {
            int totalLines = CountLines(fileContent);
            int maxStartLine = Math.Max(0, totalLines - 1);

            return Gen.Choose(0, maxStartLine).SelectMany(startLine =>
                Gen.Choose(0, 50).SelectMany(charOffset =>
                    Gen.Choose(1, 200).Select(charCount =>
                        new TestInput(fileContent, startLine, charOffset, charCount))));
        });

        return Arb.From(gen);
    }

    /// <summary>
    /// Counts lines in byte content using the same logic as FileIndex (LF, CRLF, CR delimiters).
    /// </summary>
    private static int CountLines(byte[] content)
    {
        if (content.Length == 0) return 0;

        int lines = 1;
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
    /// Splits file content into lines preserving delimiters.
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
                break;

            int pos = start;
            while (pos < text.Length && text[pos] != '\r' && text[pos] != '\n')
                pos++;

            string content = text.Substring(start, pos - start);
            string delimiter;

            if (pos >= text.Length)
            {
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
    /// Counts content characters in a response string (excludes newline delimiters).
    /// Newline delimiters are: \n, \r\n, \r
    /// </summary>
    private static int CountContentChars(string response)
    {
        int count = 0;
        for (int i = 0; i < response.Length; i++)
        {
            if (response[i] == '\n')
                continue; // LF delimiter — skip
            if (response[i] == '\r')
            {
                // CR or CRLF delimiter — skip
                if (i + 1 < response.Length && response[i + 1] == '\n')
                    i++; // skip the \n in CRLF
                continue;
            }
            count++;
        }
        return count;
    }

    /// <summary>
    /// Computes the expected response for a wrapped view request using independent logic.
    /// This mirrors the spec: read from startLine at characterOffset, collect up to
    /// characterCount content chars, include delimiters in output but don't count them.
    /// </summary>
    private static string ComputeExpectedResponse(
        List<(string content, string delimiter)> lines,
        int startLine, int characterOffset, int characterCount)
    {
        if (startLine >= lines.Count)
            return "";

        var result = new StringBuilder();
        int contentCharsCollected = 0;
        int currentLine = startLine;
        int currentOffset = characterOffset;

        while (contentCharsCollected < characterCount && currentLine < lines.Count)
        {
            var (content, delimiter) = lines[currentLine];

            // Handle offset overflow: skip lines whose content is shorter
            if (currentOffset >= content.Length)
            {
                currentOffset -= content.Length;
                currentLine++;
                continue;
            }

            // Extract characters from currentOffset
            int available = content.Length - currentOffset;
            int toTake = Math.Min(available, characterCount - contentCharsCollected);
            result.Append(content, currentOffset, toTake);
            contentCharsCollected += toTake;

            // If we consumed the entire remaining line content, append delimiter
            if (currentOffset + toTake >= content.Length && delimiter.Length > 0)
            {
                result.Append(delimiter);
            }

            currentLine++;
            currentOffset = 0;
        }

        return result.ToString();
    }

    private string CreateTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_prop6_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static async Task<FileViewService> CreateServiceAndWaitForScan(string path)
    {
        var logger = NullLogger<FileViewService>.Instance;
        var service = new FileViewService(path, CancellationToken.None, logger);

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.ScanState < ScanState.QuickScanComplete && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        return service;
    }

    /// <summary>
    /// For any generated file content and valid wrapped-mode request parameters,
    /// the response contains at most characterCount content characters (excluding delimiters).
    ///
    /// **Validates: Requirements 6.1, 6.2**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property WrappedView_ContentCharsDoNotExceedCharacterCount()
    {
        return Prop.ForAll(
            WrappedContentCountArb(),
            input =>
            {
                var path = CreateTempFile(input.FileContent);
                using var service = CreateServiceAndWaitForScan(path).Result;

                var result = service.GetWrappedViewAsync(
                    input.StartLine, input.CharacterOffset, input.CharacterCount).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                var response = result.Value;
                int contentChars = CountContentChars(response);

                return (contentChars <= input.CharacterCount)
                    .Label($"Content chars ({contentChars}) should be <= characterCount ({input.CharacterCount}). " +
                           $"Response: [{Escape(response)}]");
            });
    }

    /// <summary>
    /// For any generated file content and valid wrapped-mode request parameters,
    /// delimiters in the response appear at correct positions (between logical lines)
    /// matching what an independent extraction would produce.
    ///
    /// **Validates: Requirements 6.2, 6.3, 6.4, 6.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property WrappedView_DelimitersAtCorrectPositions()
    {
        return Prop.ForAll(
            WrappedContentCountArb(),
            input =>
            {
                var path = CreateTempFile(input.FileContent);
                using var service = CreateServiceAndWaitForScan(path).Result;

                var result = service.GetWrappedViewAsync(
                    input.StartLine, input.CharacterOffset, input.CharacterCount).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                var response = result.Value;

                // Independently compute expected response
                var lines = SplitIntoLinesWithDelimiters(input.FileContent);
                var expected = ComputeExpectedResponse(
                    lines, input.StartLine, input.CharacterOffset, input.CharacterCount);

                return (response == expected)
                    .Label($"Response mismatch.\n" +
                           $"  StartLine={input.StartLine}, CharOffset={input.CharacterOffset}, CharCount={input.CharacterCount}\n" +
                           $"  Expected: [{Escape(expected)}]\n" +
                           $"  Actual:   [{Escape(response)}]");
            });
    }

    /// <summary>
    /// If startLine is beyond the file's total line count, the response is an empty string.
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property WrappedView_StartLineBeyondFile_ReturnsEmpty()
    {
        var contentByte = Gen.Choose(0x20, 0x7E).Select(i => (byte)i);
        var lf = Gen.Constant(new byte[] { 0x0A });

        // Generate a small file with 1-5 lines
        var lineSegment = Gen.Choose(1, 30)
            .SelectMany(len => Gen.ArrayOf(contentByte, len))
            .SelectMany(content => lf.Select(ending =>
            {
                var result = new byte[content.Length + ending.Length];
                content.CopyTo(result, 0);
                ending.CopyTo(result, content.Length);
                return result;
            }));

        var fileGen = Gen.Choose(1, 5).SelectMany(lineCount =>
            Gen.ArrayOf(lineSegment, lineCount).Select(linesArr =>
            {
                var totalLen = linesArr.Sum(a => a.Length);
                var result = new byte[totalLen];
                int pos = 0;
                foreach (var chunk in linesArr)
                {
                    chunk.CopyTo(result, pos);
                    pos += chunk.Length;
                }
                return result;
            }));

        var gen = fileGen.SelectMany(fileContent =>
        {
            int totalLines = CountLines(fileContent);
            // startLine beyond file: totalLines to totalLines + 10
            return Gen.Choose(totalLines, totalLines + 10).SelectMany(startLine =>
                Gen.Choose(0, 20).SelectMany(charOffset =>
                    Gen.Choose(1, 100).Select(charCount =>
                        new TestInput(fileContent, startLine, charOffset, charCount))));
        });

        var arb = Arb.From(gen);

        return Prop.ForAll(
            arb,
            input =>
            {
                var path = CreateTempFile(input.FileContent);
                using var service = CreateServiceAndWaitForScan(path).Result;

                var result = service.GetWrappedViewAsync(
                    input.StartLine, input.CharacterOffset, input.CharacterCount).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                return (result.Value == "")
                    .Label($"Expected empty string for startLine={input.StartLine} beyond file, " +
                           $"got: [{Escape(result.Value)}]");
            });
    }

    /// <summary>
    /// Content characters collected match what's in the source file at the specified position.
    /// The non-delimiter characters in the response, when concatenated, equal the content
    /// characters from the source file starting at the specified line and offset.
    ///
    /// **Validates: Requirements 6.1, 6.3, 6.4, 6.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property WrappedView_ContentCharsMatchSourceFile()
    {
        return Prop.ForAll(
            WrappedContentCountArb(),
            input =>
            {
                var path = CreateTempFile(input.FileContent);
                using var service = CreateServiceAndWaitForScan(path).Result;

                var result = service.GetWrappedViewAsync(
                    input.StartLine, input.CharacterOffset, input.CharacterCount).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                var response = result.Value;

                // Extract only content chars from response (skip delimiters)
                var responseContentChars = ExtractContentChars(response);

                // Independently compute expected content chars from source
                var lines = SplitIntoLinesWithDelimiters(input.FileContent);
                var expectedContentChars = ExtractExpectedContentChars(
                    lines, input.StartLine, input.CharacterOffset, input.CharacterCount);

                return (responseContentChars == expectedContentChars)
                    .Label($"Content chars mismatch.\n" +
                           $"  StartLine={input.StartLine}, CharOffset={input.CharacterOffset}, CharCount={input.CharacterCount}\n" +
                           $"  Expected content: [{Escape(expectedContentChars)}]\n" +
                           $"  Actual content:   [{Escape(responseContentChars)}]");
            });
    }

    /// <summary>
    /// Extracts only content characters from a response (skips newline delimiters).
    /// </summary>
    private static string ExtractContentChars(string response)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < response.Length; i++)
        {
            if (response[i] == '\n')
                continue;
            if (response[i] == '\r')
            {
                if (i + 1 < response.Length && response[i + 1] == '\n')
                    i++;
                continue;
            }
            sb.Append(response[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Computes expected content characters from source file lines.
    /// </summary>
    private static string ExtractExpectedContentChars(
        List<(string content, string delimiter)> lines,
        int startLine, int characterOffset, int characterCount)
    {
        if (startLine >= lines.Count)
            return "";

        var result = new StringBuilder();
        int contentCharsCollected = 0;
        int currentLine = startLine;
        int currentOffset = characterOffset;

        while (contentCharsCollected < characterCount && currentLine < lines.Count)
        {
            var (content, _) = lines[currentLine];

            if (currentOffset >= content.Length)
            {
                currentOffset -= content.Length;
                currentLine++;
                continue;
            }

            int available = content.Length - currentOffset;
            int toTake = Math.Min(available, characterCount - contentCharsCollected);
            result.Append(content, currentOffset, toTake);
            contentCharsCollected += toTake;

            currentLine++;
            currentOffset = 0;
        }

        return result.ToString();
    }

    private static string Escape(string s)
    {
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
