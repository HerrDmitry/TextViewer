using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 4: Char-length usage in wrapped line count computation.
/// For any LineIndex with char lengths set via AppendLinePairs, ComputeWrappedLineCount
/// SHALL use GetCharLength for each line's visual row computation.
///
/// Feature: wrapped-line-count, Property 4: Char-length in visual rows
///
/// **Validates: Requirements 1.4**
/// </summary>
public class WrappedLineCountCharFallbackPropertyTests
{
    /// <summary>
    /// Test case: LinePairs with explicit byte and char lengths, colCount for wrapping.
    /// </summary>
    private sealed record TestCase(
        LinePair[] Pairs,
        int ColCount);

    /// <summary>
    /// Generates LineIndex with various char lengths (charLength &lt;= byteLength).
    /// ByteLengths: 2-50 lines, values 0-500.
    /// CharLengths: values &lt;= corresponding byte length.
    /// ColCount: 1-100.
    /// </summary>
    private static Arbitrary<TestCase> CharLengthTestCases()
    {
        var gen = Gen.Choose(2, 50).SelectMany(lineCount =>
            Gen.ArrayOf(
                Gen.Choose(0, 500).Select(v => (ulong)v),
                lineCount)
            .SelectMany(byteLengths =>
            {
                // Generate char lengths, each <= byte length
                var charGen = Gen.ArrayOf(
                    Gen.Choose(0, 500).Select(v => (ulong)v),
                    byteLengths.Length)
                    .Select(charLens =>
                    {
                        // Clamp each char length to <= byte length
                        for (int i = 0; i < charLens.Length; i++)
                        {
                            if (charLens[i] > byteLengths[i])
                                charLens[i] = byteLengths[i];
                        }
                        return charLens;
                    });

                return charGen.SelectMany(charLengths =>
                    Gen.Choose(1, 100)
                        .Select(col =>
                        {
                            var pairs = new LinePair[byteLengths.Length];
                            for (int i = 0; i < byteLengths.Length; i++)
                            {
                                pairs[i] = new LinePair(byteLengths[i], charLengths[i]);
                            }
                            return new TestCase(pairs, col);
                        }));
            }));

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 4: Char-length usage
    ///
    /// For any LineIndex with char lengths set via AppendLinePairs, ComputeWrappedLineCount
    /// SHALL produce the same result as computing sequentially with the rule:
    /// use charLen for each line; then ceil(len/colCount) or 1 if len==0.
    ///
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property CharLength_UsedForVisualRowComputation()
    {
        return Prop.ForAll(
            CharLengthTestCases(),
            (TestCase tc) =>
            {
                // Build a real LineIndex with the specified pairs
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(tc.Pairs);

                // Compute expected result using char lengths
                long expected = 0;
                for (int i = 0; i < tc.Pairs.Length; i++)
                {
                    long len = (long)tc.Pairs[i].CharLength;
                    expected += len == 0 ? 1 : (len + tc.ColCount - 1) / tc.ColCount;
                }

                // Compute actual
                long actual = Program.ComputeWrappedLineCount(lineIndex, tc.Pairs.Length, tc.ColCount);

                return (actual == expected).Label(
                    $"Expected {expected}, got {actual}. ColCount={tc.ColCount}, Lines={tc.Pairs.Length}");
            });
    }
}
