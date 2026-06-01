using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 4: Char-length fallback.
/// For any line where GetCharLength returns null, the handler SHALL use GetByteLength
/// for that line's visual row computation, producing the same result as if charLen
/// were the byte length value.
///
/// Feature: wrapped-line-count, Property 4: Char-length fallback
///
/// **Validates: Requirements 1.4**
/// </summary>
public class WrappedLineCountCharFallbackPropertyTests
{
    /// <summary>
    /// Test case: byte lengths for all lines, char lengths written for a prefix [0..writeUpTo),
    /// remaining lines have null char length (fallback to byte length).
    /// </summary>
    private sealed record TestCase(
        ulong[] ByteLengths,
        ulong[] CharLengthsPrefix,
        int WriteUpTo,
        int ColCount);

    /// <summary>
    /// Generates LineIndex with mixed null/non-null char lengths.
    /// ByteLengths: 2-50 lines, values 0-500.
    /// WriteUpTo: 0 to lineCount-1 (at least one line without char length).
    /// CharLengthsPrefix: values &lt;= corresponding byte length for [0..writeUpTo).
    /// ColCount: 1-100.
    /// </summary>
    private static Arbitrary<TestCase> CharFallbackTestCases()
    {
        var gen = Gen.Choose(2, 50).SelectMany(lineCount =>
            Gen.ArrayOf(
                Gen.Choose(0, 500).Select(v => (ulong)v),
                lineCount)
            .SelectMany(byteLengths =>
                // writeUpTo: 0 to lineCount-1 ensures at least one null line
                Gen.Choose(0, byteLengths.Length - 1).SelectMany(writeUpTo =>
                {
                    if (writeUpTo == 0)
                    {
                        // No char lengths written - all lines fall back
                        return Gen.Choose(1, 100)
                            .Select(col => new TestCase(byteLengths, Array.Empty<ulong>(), 0, col));
                    }

                    // Generate char lengths for the prefix, each <= byte length
                    return Gen.ArrayOf(
                        Gen.Choose(0, 500).Select(v => (ulong)v),
                        writeUpTo)
                    .Select(charLens =>
                    {
                        // Clamp each char length to <= byte length
                        for (int i = 0; i < charLens.Length; i++)
                        {
                            if (charLens[i] > byteLengths[i])
                                charLens[i] = byteLengths[i];
                        }
                        return charLens;
                    })
                    .SelectMany(charLens =>
                        Gen.Choose(1, 100)
                            .Select(col => new TestCase(byteLengths, charLens, writeUpTo, col)));
                })));

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 4: Char-length fallback
    ///
    /// For any LineIndex with mixed null/non-null char lengths, ComputeWrappedLineCount
    /// SHALL produce the same result as computing sequentially with the rule:
    /// use charLen if available, else use byteLen; then ceil(len/colCount) or 1 if len==0.
    ///
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property CharLengthFallback_UsesByteLength_WhenCharLengthNull()
    {
        return Prop.ForAll(
            CharFallbackTestCases(),
            (TestCase tc) =>
            {
                // Build a real LineIndex with the specified byte lengths
                var lineIndex = new LineIndex();
                lineIndex.AppendByteLengths(tc.ByteLengths);

                // Write char lengths for the prefix [0..writeUpTo)
                for (int i = 0; i < tc.WriteUpTo; i++)
                {
                    lineIndex.SetCharLength(i, tc.CharLengthsPrefix[i]);
                }

                // Compute expected result using the fallback rule
                long expected = 0;
                for (int i = 0; i < tc.ByteLengths.Length; i++)
                {
                    long len;
                    if (i < tc.WriteUpTo)
                    {
                        // Char length is available
                        len = (long)tc.CharLengthsPrefix[i];
                    }
                    else
                    {
                        // Fallback to byte length
                        len = (long)tc.ByteLengths[i];
                    }
                    expected += len == 0 ? 1 : (len + tc.ColCount - 1) / tc.ColCount;
                }

                // Compute actual
                long actual = Program.ComputeWrappedLineCount(lineIndex, tc.ByteLengths.Length, tc.ColCount);

                return (actual == expected).Label(
                    $"Expected {expected}, got {actual}. ColCount={tc.ColCount}, Lines={tc.ByteLengths.Length}, WriteUpTo={tc.WriteUpTo}");
            });
    }
}
