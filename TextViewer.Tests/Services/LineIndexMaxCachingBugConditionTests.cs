using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Bug condition exploration tests for LineIndex max caching.
/// These tests assert that LineIndex exposes MaxByteLength and MaxCharLength
/// properties that return correct cached maximums.
/// On UNFIXED code, these tests will NOT COMPILE because the properties don't exist.
///
/// **Validates: Requirements 1.1, 1.2, 2.1, 2.2, 2.3**
/// </summary>
public class LineIndexMaxCachingBugConditionTests
{
    /// <summary>
    /// Generates random byte length arrays with 1–50 elements, values 1–100000.
    /// </summary>
    private static Arbitrary<ulong[]> ByteLengthSpans()
    {
        var gen = Gen.Choose(1, 50)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100000).Select(v => (ulong)v),
                    len));

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates random char length arrays with 1–50 elements, values 1–100000.
    /// </summary>
    private static Arbitrary<ulong[]> CharLengthSpans()
    {
        var gen = Gen.Choose(1, 50)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100000).Select(v => (ulong)v),
                    len));

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 1: Bug Condition - MaxByteLength Equals Iteration Maximum
    ///
    /// For any random ulong[] byte-length span appended to LineIndex,
    /// MaxByteLength must equal the maximum value in the span.
    ///
    /// **Validates: Requirements 1.1, 1.2, 2.1**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MaxByteLength_Equals_IterationMaximum()
    {
        return Prop.ForAll(
            ByteLengthSpans(),
            (ulong[] byteLengths) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendByteLengths(byteLengths);

                var expected = byteLengths.Max();
                var actual = lineIndex.MaxByteLength;

                return (actual == expected).Label(
                    $"MaxByteLength = {actual}, expected {expected}");
            });
    }

    /// <summary>
    /// Property 1: Bug Condition - MaxCharLength Equals Iteration Maximum
    ///
    /// For any random char lengths written to LineIndex lines 0..N-1,
    /// MaxCharLength must equal the maximum of all written char lengths.
    ///
    /// **Validates: Requirements 2.2, 2.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MaxCharLength_Equals_IterationMaximum()
    {
        return Prop.ForAll(
            ByteLengthSpans(),
            CharLengthSpans(),
            (ulong[] byteLengths, ulong[] charLengths) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendByteLengths(byteLengths);

                // Write char lengths for lines 0..min(byteLengths.Length, charLengths.Length)-1
                int charCount = Math.Min(byteLengths.Length, charLengths.Length);
                for (int i = 0; i < charCount; i++)
                {
                    lineIndex.SetCharLength(i, charLengths[i]);
                }

                var expected = charLengths.Take(charCount).Max();
                var actual = lineIndex.MaxCharLength;

                return (actual == expected).Label(
                    $"MaxCharLength = {actual}, expected {expected}");
            });
    }

    /// <summary>
    /// Property 1: Bug Condition - MaxCharLength Null When No Char Lengths Written
    ///
    /// When no char lengths have been written, MaxCharLength must be null.
    ///
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MaxCharLength_Null_WhenNoCharLengthsWritten()
    {
        return Prop.ForAll(
            ByteLengthSpans(),
            (ulong[] byteLengths) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendByteLengths(byteLengths);

                var actual = lineIndex.MaxCharLength;

                return (actual == null).Label(
                    $"MaxCharLength should be null when no char lengths written, got {actual}");
            });
    }
}
