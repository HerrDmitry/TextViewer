using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Bug condition exploration tests for LineIndex max caching.
/// These tests verify that LineIndex exposes MaxByteLength and MaxCharLength
/// properties that return correct cached maximums.
///
/// **Validates: Requirements 1.1, 1.2, 2.1, 2.2, 2.3**
/// </summary>
public class LineIndexMaxCachingBugConditionTests
{
    /// <summary>
    /// Generates random LinePair arrays with 1–50 elements, byte values 1–100000,
    /// char values &lt;= byte values.
    /// </summary>
    private static Arbitrary<LinePair[]> LinePairSpans()
    {
        var gen = Gen.Choose(1, 50)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100000).Select(v => (ulong)v),
                    len))
            .Select(byteLengths => byteLengths.Select(b =>
                new LinePair(b, b > 0 ? b - 1 : 0)).ToArray());

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates random LinePair arrays with explicit char lengths.
    /// </summary>
    private static Arbitrary<LinePair[]> LinePairSpansWithCharLengths()
    {
        var gen = Gen.Choose(1, 50)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100000).Select(v => (ulong)v),
                    len)
                .SelectMany(byteLengths =>
                    Gen.ArrayOf(
                        Gen.Choose(1, 100000).Select(v => (ulong)v),
                        byteLengths.Length)
                    .Select(charLengths =>
                    {
                        var pairs = new LinePair[byteLengths.Length];
                        for (int i = 0; i < byteLengths.Length; i++)
                        {
                            pairs[i] = new LinePair(byteLengths[i], charLengths[i]);
                        }
                        return pairs;
                    })));

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 1: Bug Condition - MaxByteLength Equals Iteration Maximum
    ///
    /// For any random LinePair[] appended to LineIndex,
    /// MaxByteLength must equal the maximum byte length value in the span.
    ///
    /// **Validates: Requirements 1.1, 1.2, 2.1**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MaxByteLength_Equals_IterationMaximum()
    {
        return Prop.ForAll(
            LinePairSpans(),
            (LinePair[] pairs) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                var expected = pairs.Max(p => p.ByteLength);
                var actual = lineIndex.MaxByteLength;

                return (actual == expected).Label(
                    $"MaxByteLength = {actual}, expected {expected}");
            });
    }

    /// <summary>
    /// Property 1: Bug Condition - MaxCharLength Equals Iteration Maximum
    ///
    /// For any random LinePair[] appended to LineIndex,
    /// MaxCharLength must equal the maximum char length value.
    ///
    /// **Validates: Requirements 2.2, 2.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MaxCharLength_Equals_IterationMaximum()
    {
        return Prop.ForAll(
            LinePairSpansWithCharLengths(),
            (LinePair[] pairs) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                var expected = pairs.Max(p => p.CharLength);
                var actual = lineIndex.MaxCharLength;

                return (actual == expected).Label(
                    $"MaxCharLength = {actual}, expected {expected}");
            });
    }

    /// <summary>
    /// Property 1: Bug Condition - MaxCharLength Zero When No Lines Appended
    ///
    /// When no lines have been appended, MaxCharLength must be 0 (non-nullable ulong).
    ///
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MaxCharLength_Zero_WhenNoLinesAppended()
    {
        return Prop.ForAll(
            LinePairSpans(),
            (LinePair[] _) =>
            {
                var lineIndex = new LineIndex();
                // Don't append anything

                var actual = lineIndex.MaxCharLength;

                return (actual == 0UL).Label(
                    $"MaxCharLength should be 0 when no lines appended, got {actual}");
            });
    }
}
