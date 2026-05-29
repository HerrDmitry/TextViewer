using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 5: Column counting — code units counted, delimiters excluded
/// For any row extraction, the number of content characters (before the delimiter) in the output
/// SHALL be at most colCount .NET chars (UTF-16 code units), and the appended delimiter SHALL not
/// reduce the content character budget — i.e., delimiter bytes are appended verbatim but never
/// counted toward colCount.
///
/// **Validates: Requirements 5.3, 5.4**
/// </summary>
public class FileViewServiceColumnCountPropertyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFileFromBytes(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fvs_prop5_{Guid.NewGuid():N}.txt");
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
    /// Generates random content strings containing ASCII chars, surrogate pairs (emoji),
    /// and control chars (tab), paired with a random delimiter and a random colCount.
    /// </summary>
    private static Arbitrary<(string content, string delimiter, int colCount)> ColumnCountArb()
    {
        // Generate random content with a mix of ASCII, surrogates, and control chars
        var asciiChar = Gen.Choose(0x20, 0x7E).Select(c => ((char)c).ToString());  // ASCII printable
        var tabChar = Gen.Constant("\t");                                            // Tab (control char)
        var emojiString = Gen.Elements(                                             // Surrogate pairs (emoji)
            "\uD83C\uDF89",  // 🎉 (2 code units)
            "\uD83D\uDE00",  // 😀 (2 code units)
            "\uD83D\uDC4D"   // 👍 (2 code units)
        );

        // Mix: ~70% ASCII, ~10% tab, ~20% emoji
        var contentCharGen = Gen.OneOf(
            asciiChar, asciiChar, asciiChar, asciiChar, asciiChar, asciiChar, asciiChar,
            tabChar,
            emojiString, emojiString
        );

        // Generate a content string (1-60 character units)
        var contentGen = Gen.Choose(1, 60)
            .SelectMany(len => Gen.ArrayOf(contentCharGen, len))
            .Select(parts => string.Concat(parts));

        var delimiterGen = Gen.Elements("\n", "\r\n", "\r");

        var colCountGen = Gen.Choose(1, 50);

        var gen = contentGen.SelectMany(content =>
            delimiterGen.SelectMany(delimiter =>
                colCountGen.Select(colCount =>
                    (content, delimiter, colCount))));

        return Arb.From(gen);
    }

    /// <summary>
    /// For any generated string with surrogates, control chars, and various delimiters:
    /// - The content portion of the result (everything before the delimiter) has length ≤ colCount
    /// - The delimiter is appended after the content (not counted toward colCount)
    ///
    /// **Validates: Requirements 5.3, 5.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ContentLength_DoesNotExceed_ColCount()
    {
        return Prop.ForAll(
            ColumnCountArb(),
            args =>
            {
                var (content, delimiter, colCount) = args;

                // Encode as UTF-8 and write to temp file
                var lineText = content + delimiter;
                var lineBytes = Encoding.UTF8.GetBytes(lineText);
                var path = CreateTempFileFromBytes(lineBytes);

                // Create service and wait for scan
                using var service = CreateServiceAndWaitForScan(path).Result;

                // Request with startCol=0 and the random colCount
                var result = service.GetViewAsync(0, 0, 1, colCount).Result;

                if (!result.IsSuccess)
                    return false.Label($"Expected success but got error: {result.Error.Message}");

                var row = result.Value.Rows[0];

                // Determine the delimiter in the output (should be at the end)
                string outputDelimiter = "";
                if (row.EndsWith("\r\n"))
                    outputDelimiter = "\r\n";
                else if (row.EndsWith("\n"))
                    outputDelimiter = "\n";
                else if (row.EndsWith("\r"))
                    outputDelimiter = "\r";

                // Content is everything before the delimiter
                var outputContent = row.Length >= outputDelimiter.Length
                    ? row.Substring(0, row.Length - outputDelimiter.Length)
                    : row;

                // Assert 1: content length ≤ colCount code units
                var contentLenOk = outputContent.Length <= colCount;

                // Assert 2: delimiter is appended (matches the original delimiter)
                var delimiterOk = outputDelimiter == delimiter;

                return (contentLenOk && delimiterOk)
                    .Label($"content='{content}' (len={content.Length}), delimiter='{EscapeDelimiter(delimiter)}', " +
                           $"colCount={colCount}, outputContent.Length={outputContent.Length}, " +
                           $"outputDelimiter='{EscapeDelimiter(outputDelimiter)}' | " +
                           $"contentLenOk={contentLenOk}, delimiterOk={delimiterOk}");
            });
    }

    private static string EscapeDelimiter(string d) => d switch
    {
        "\r\n" => "\\r\\n",
        "\n" => "\\n",
        "\r" => "\\r",
        "" => "(none)",
        _ => d
    };
}
