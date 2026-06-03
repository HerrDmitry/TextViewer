using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Preservation property tests for LineIndex max caching bugfix.
/// These tests verify existing LineIndex behavior is unchanged:
/// - GetByteLength(i) returns correct values after AppendLinePairs
/// - GetCharLength(i) returns correct values after AppendLinePairs
/// - LineCount equals total appended lines
///
/// **Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5**
/// </summary>
public class LineIndexMaxCachingPreservationTests
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
    /// Generates LinePair arrays with explicit (byteLength, charLength) pairs
    /// where charLength &lt;= byteLength.
    /// </summary>
    private static Arbitrary<LinePair[]> LinePairSpansWithCharLengths()
    {
        var gen = Gen.Choose(1, 50)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100000).Select(v => (ulong)v),
                    len))
            .SelectMany(byteLengths =>
            {
                var charGen = Gen.ArrayOf<int>(
                    Gen.Choose(0, int.MaxValue),
                    byteLengths.Length)
                    .Select(rands =>
                    {
                        var pairs = new LinePair[byteLengths.Length];
                        for (int i = 0; i < byteLengths.Length; i++)
                        {
                            ulong charVal = (ulong)(rands[i] % (int)Math.Min(byteLengths[i], 100000)) + 1;
                            pairs[i] = new LinePair(byteLengths[i], charVal);
                        }
                        return pairs;
                    });
                return charGen;
            });

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 2: Preservation - GetByteLength returns correct values
    ///
    /// For any random LinePair[] appended to LineIndex,
    /// GetByteLength(i) must return the exact byte length from the input for each i.
    ///
    /// **Validates: Requirements 3.1, 3.2**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property GetByteLength_Returns_CorrectValues_AfterAppend()
    {
        return Prop.ForAll(
            LinePairSpans(),
            (LinePair[] pairs) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                for (int i = 0; i < pairs.Length; i++)
                {
                    var actual = lineIndex.GetByteLength(i);
                    if (actual != pairs[i].ByteLength)
                    {
                        return false.Label(
                            $"GetByteLength({i}) = {actual}, expected {pairs[i].ByteLength}");
                    }
                }

                return true.Label("All byte lengths match input");
            });
    }

    /// <summary>
    /// Property 2: Preservation - GetCharLength returns correct values after AppendLinePairs
    ///
    /// For any random LinePair[] appended to LineIndex,
    /// GetCharLength(i) must return the exact char length from the input.
    ///
    /// **Validates: Requirements 3.3, 3.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property GetCharLength_Returns_CorrectValues_AfterAppend()
    {
        return Prop.ForAll(
            LinePairSpansWithCharLengths(),
            (LinePair[] pairs) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                for (int i = 0; i < pairs.Length; i++)
                {
                    var actual = lineIndex.GetCharLength(i);
                    if (actual != pairs[i].CharLength)
                    {
                        return false.Label(
                            $"GetCharLength({i}) = {actual}, expected {pairs[i].CharLength}");
                    }
                }

                return true.Label("All char lengths match input");
            });
    }

    /// <summary>
    /// Property 2: Preservation - LineCount equals total appended lines
    ///
    /// For any random LinePair[] appended to LineIndex,
    /// LineCount must equal the length of the appended array.
    ///
    /// **Validates: Requirements 3.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property LineCount_Equals_TotalAppendedLines()
    {
        return Prop.ForAll(
            LinePairSpans(),
            (LinePair[] pairs) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendLinePairs(pairs);

                var actual = lineIndex.LineCount;

                return (actual == pairs.Length).Label(
                    $"LineCount = {actual}, expected {pairs.Length}");
            });
    }
}
