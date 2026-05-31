using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Preservation property tests for LineIndex max caching bugfix.
/// These tests verify existing LineIndex behavior is unchanged:
/// - GetByteLength(i) returns correct values after AppendByteLengths
/// - GetCharLength(i) returns correct values after SetCharLength
/// - LineCount equals total appended lines
///
/// Run on UNFIXED code: tests MUST PASS (confirms baseline to preserve).
///
/// **Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5**
/// </summary>
public class LineIndexMaxCachingPreservationTests
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
    /// Generates a pair of (byteLengths, charLengths) where each charLength[i] &lt;= byteLengths[i].
    /// This respects the invariant that Char_Length ≤ Byte_Length for every line.
    /// </summary>
    private static Arbitrary<(ulong[] ByteLengths, ulong[] CharLengths)> ByteAndCharLengthPairs()
    {
        var gen = Gen.Choose(1, 50)
            .SelectMany(len =>
                Gen.ArrayOf(
                    Gen.Choose(1, 100000).Select(v => (ulong)v),
                    len))
            .SelectMany(byteLengths =>
            {
                // Generate char lengths where each is <= corresponding byte length
                var charGen = Gen.ArrayOf<int>(
                    Gen.Choose(0, int.MaxValue),
                    byteLengths.Length)
                    .Select(rands =>
                    {
                        var chars = new ulong[byteLengths.Length];
                        for (int i = 0; i < byteLengths.Length; i++)
                        {
                            // Map random int to range [1, byteLengths[i]]
                            chars[i] = (ulong)(rands[i] % (int)Math.Min(byteLengths[i], 100000)) + 1;
                        }
                        return chars;
                    });
                return charGen.Select(chars => (byteLengths, chars));
            });

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 2: Preservation - GetByteLength returns correct values
    ///
    /// For any random ulong[] byte-length span appended to LineIndex,
    /// GetByteLength(i) must return the exact value from the input array for each i.
    ///
    /// **Validates: Requirements 3.1, 3.2**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property GetByteLength_Returns_CorrectValues_AfterAppend()
    {
        return Prop.ForAll(
            ByteLengthSpans(),
            (ulong[] byteLengths) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendByteLengths(byteLengths);

                for (int i = 0; i < byteLengths.Length; i++)
                {
                    var actual = lineIndex.GetByteLength(i);
                    if (actual != byteLengths[i])
                    {
                        return false.Label(
                            $"GetByteLength({i}) = {actual}, expected {byteLengths[i]}");
                    }
                }

                return true.Label("All byte lengths match input");
            });
    }

    /// <summary>
    /// Property 2: Preservation - GetCharLength returns correct values after SetCharLength
    ///
    /// For any random char lengths set on LineIndex lines (where charLength &lt;= byteLength),
    /// GetCharLength(i) must return the exact value that was set.
    ///
    /// **Validates: Requirements 3.3, 3.4**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property GetCharLength_Returns_CorrectValues_AfterSet()
    {
        return Prop.ForAll(
            ByteAndCharLengthPairs(),
            (pair) =>
            {
                var (byteLengths, charLengths) = pair;
                var lineIndex = new LineIndex();
                lineIndex.AppendByteLengths(byteLengths);

                int charCount = Math.Min(byteLengths.Length, charLengths.Length);
                for (int i = 0; i < charCount; i++)
                {
                    lineIndex.SetCharLength(i, charLengths[i]);
                }

                for (int i = 0; i < charCount; i++)
                {
                    var actual = lineIndex.GetCharLength(i);
                    if (actual != charLengths[i])
                    {
                        return false.Label(
                            $"GetCharLength({i}) = {actual}, expected {charLengths[i]}");
                    }
                }

                return true.Label("All char lengths match input");
            });
    }

    /// <summary>
    /// Property 2: Preservation - LineCount equals total appended lines
    ///
    /// For any random ulong[] byte-length span appended to LineIndex,
    /// LineCount must equal the length of the appended array.
    ///
    /// **Validates: Requirements 3.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property LineCount_Equals_TotalAppendedLines()
    {
        return Prop.ForAll(
            ByteLengthSpans(),
            (ulong[] byteLengths) =>
            {
                var lineIndex = new LineIndex();
                lineIndex.AppendByteLengths(byteLengths);

                var actual = lineIndex.LineCount;

                return (actual == byteLengths.Length).Label(
                    $"LineCount = {actual}, expected {byteLengths.Length}");
            });
    }
}
