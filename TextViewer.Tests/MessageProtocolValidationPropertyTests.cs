using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Property 14: Validation rejects invalid fields (C#)
/// Validates: Requirements 8.9, 8.10, 15.1, 15.2, 15.3, 15.5, 15.6
/// </summary>
public class MessageProtocolValidationPropertyTests
{
    // --- Generators ---

    private static Gen<string> GenStringFromChars(char[] chars, int minLen, int maxLen)
    {
        return Gen.Choose(minLen, maxLen)
            .SelectMany(len =>
                Gen.ArrayOf(Gen.Elements(chars), len)
                    .Select(arr => new string(arr)));
    }

    /// <summary>
    /// Generates valid Message_Type strings: [a-z0-9:-]+, 1–64 chars.
    /// </summary>
    private static Arbitrary<string> ValidMessageTypeArb()
    {
        var chars = "abcdefghijklmnopqrstuvwxyz0123456789:-".ToCharArray();
        return Arb.From(GenStringFromChars(chars, 1, 64));
    }

    /// <summary>
    /// Generates invalid Message_Type strings: contains uppercase, spaces, underscores,
    /// special chars, empty, or >64 chars.
    /// </summary>
    private static Arbitrary<string> InvalidMessageTypeArb()
    {
        var empty = Gen.Constant(string.Empty);

        var tooLong = GenStringFromChars(
            "abcdefghijklmnopqrstuvwxyz0123456789:-".ToCharArray(), 65, 128);

        var withUppercase = GenStringFromChars(
            "abcABCDEF0123456789:-".ToCharArray(), 1, 64)
            .Where(s => s.Any(char.IsUpper));

        var withSpaces = GenStringFromChars(
            "abc 012:-".ToCharArray(), 1, 64)
            .Where(s => s.Contains(' '));

        var withUnderscores = GenStringFromChars(
            "abc_012:-".ToCharArray(), 1, 64)
            .Where(s => s.Contains('_'));

        var withSpecialChars = GenStringFromChars(
            "abc!@#$%^&*()012".ToCharArray(), 1, 64)
            .Where(s => s.Any(c => "!@#$%^&*()".Contains(c)));

        return Arb.From(Gen.OneOf(empty, tooLong, withUppercase, withSpaces, withUnderscores, withSpecialChars));
    }

    /// <summary>
    /// Generates valid Correlation_ID strings: [a-zA-Z0-9-]+, 1–36 chars.
    /// </summary>
    private static Arbitrary<string> ValidCorrelationIdArb()
    {
        var chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-".ToCharArray();
        return Arb.From(GenStringFromChars(chars, 1, 36));
    }

    /// <summary>
    /// Generates invalid Correlation_ID strings: contains colons, spaces, underscores,
    /// special chars, empty, or >36 chars.
    /// </summary>
    private static Arbitrary<string> InvalidCorrelationIdArb()
    {
        var empty = Gen.Constant(string.Empty);

        var tooLong = GenStringFromChars(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-".ToCharArray(), 37, 72);

        var withColons = GenStringFromChars(
            "abc:ABC012-".ToCharArray(), 1, 36)
            .Where(s => s.Contains(':'));

        var withSpaces = GenStringFromChars(
            "abc ABC012-".ToCharArray(), 1, 36)
            .Where(s => s.Contains(' '));

        var withUnderscores = GenStringFromChars(
            "abc_ABC012-".ToCharArray(), 1, 36)
            .Where(s => s.Contains('_'));

        var withSpecialChars = GenStringFromChars(
            "abc!@#$%^&*()ABC012-".ToCharArray(), 1, 36)
            .Where(s => s.Any(c => "!@#$%^&*()".Contains(c)));

        return Arb.From(Gen.OneOf(empty, tooLong, withColons, withSpaces, withUnderscores, withSpecialChars));
    }

    /// <summary>
    /// Generates valid payload strings: ≤2,097,152 chars (use smaller sizes for perf).
    /// </summary>
    private static Arbitrary<string> ValidPayloadArb()
    {
        var gen = Gen.Choose(0, 1000)
            .SelectMany(len =>
                Gen.ArrayOf(Gen.Choose(32, 126).Select(i => (char)i), len)
                    .Select(arr => new string(arr)));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates oversized payload strings: >2,097,152 chars.
    /// </summary>
    private static Arbitrary<string> OversizedPayloadArb()
    {
        // Generate payloads just over the limit to keep test perf reasonable
        var gen = Gen.Choose(2_097_153, 2_097_200)
            .Select(len => new string('x', len));
        return Arb.From(gen);
    }

    // --- Property Tests ---

    /// <summary>
    /// ValidateMessageType rejects invalid: Generate strings with uppercase, spaces,
    /// underscores, special chars, empty, >64 chars → assert returns false.
    /// 
    /// **Validates: Requirements 8.10, 15.1**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ValidateMessageType_RejectsInvalid()
    {
        return Prop.ForAll(
            InvalidMessageTypeArb(),
            invalidType => !MessageProtocol.ValidateMessageType(invalidType));
    }

    /// <summary>
    /// ValidateMessageType accepts valid: Generate strings matching [a-z0-9:-]+,
    /// 1–64 chars → assert returns true.
    /// 
    /// **Validates: Requirements 8.10, 15.1**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ValidateMessageType_AcceptsValid()
    {
        return Prop.ForAll(
            ValidMessageTypeArb(),
            validType => MessageProtocol.ValidateMessageType(validType));
    }

    /// <summary>
    /// ValidateCorrelationId rejects invalid: Generate strings with colons, spaces,
    /// underscores, special chars, empty, >36 chars → assert returns false.
    /// 
    /// **Validates: Requirements 8.9, 15.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ValidateCorrelationId_RejectsInvalid()
    {
        return Prop.ForAll(
            InvalidCorrelationIdArb(),
            invalidId => !MessageProtocol.ValidateCorrelationId(invalidId));
    }

    /// <summary>
    /// ValidateCorrelationId accepts valid: Generate strings matching [a-zA-Z0-9-]+,
    /// 1–36 chars → assert returns true.
    /// 
    /// **Validates: Requirements 8.9, 15.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ValidateCorrelationId_AcceptsValid()
    {
        return Prop.ForAll(
            ValidCorrelationIdArb(),
            validId => MessageProtocol.ValidateCorrelationId(validId));
    }

    /// <summary>
    /// ValidatePayload rejects oversized: Generate strings >2,097,152 chars → assert returns false.
    /// 
    /// **Validates: Requirements 15.3, 15.5, 15.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ValidatePayload_RejectsOversized()
    {
        return Prop.ForAll(
            OversizedPayloadArb(),
            oversizedPayload => !MessageProtocol.ValidatePayload(oversizedPayload));
    }

    /// <summary>
    /// ValidatePayload accepts valid: Generate strings ≤2,097,152 chars → assert returns true.
    /// 
    /// **Validates: Requirements 15.3, 15.5, 15.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ValidatePayload_AcceptsValid()
    {
        return Prop.ForAll(
            ValidPayloadArb(),
            validPayload => MessageProtocol.ValidatePayload(validPayload));
    }
}
