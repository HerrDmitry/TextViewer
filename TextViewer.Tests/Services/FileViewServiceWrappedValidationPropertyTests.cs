using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 7: Backend wrapped extraction parameter validation
/// For any wrapped-mode view request where startLine &lt; 0, characterOffset &lt; 0,
/// or characterCount &lt; 1, the service SHALL return a Failure result with an error
/// message starting with "ERROR:" identifying the first invalid parameter.
///
/// **Validates: Requirements 6.7**
/// </summary>
public class FileViewServiceWrappedValidationPropertyTests
{
    /// <summary>
    /// Uses a non-existent file path. If validation rejects before I/O,
    /// the service will never attempt to open the file, so a non-existent path
    /// should still return the parameter validation error (not FileNotAccessible).
    /// </summary>
    private const string NonExistentPath = @"C:\__does_not_exist__\no_file.txt";

    private static FileViewService CreateService()
    {
        var logger = NullLogger<FileViewService>.Instance;
        return new FileViewService(NonExistentPath, CancellationToken.None, logger);
    }

    /// <summary>
    /// Generates (startLine, characterOffset, characterCount) where startLine is negative.
    /// Other parameters are valid.
    /// </summary>
    private static Arbitrary<(int startLine, int characterOffset, int characterCount)> NegativeStartLineArb()
    {
        var gen = Gen.Choose(int.MinValue / 2, -1).SelectMany(startLine =>
            Gen.Choose(0, 1000).SelectMany(charOffset =>
                Gen.Choose(1, 1000).Select(charCount =>
                    (startLine, charOffset, charCount))));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates (startLine, characterOffset, characterCount) where characterOffset is negative.
    /// startLine is valid (≥ 0).
    /// </summary>
    private static Arbitrary<(int startLine, int characterOffset, int characterCount)> NegativeCharacterOffsetArb()
    {
        var gen = Gen.Choose(0, 1000).SelectMany(startLine =>
            Gen.Choose(int.MinValue / 2, -1).SelectMany(charOffset =>
                Gen.Choose(1, 1000).Select(charCount =>
                    (startLine, charOffset, charCount))));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates (startLine, characterOffset, characterCount) where characterCount is less than 1.
    /// startLine and characterOffset are valid.
    /// </summary>
    private static Arbitrary<(int startLine, int characterOffset, int characterCount)> InvalidCharacterCountArb()
    {
        var gen = Gen.Choose(0, 1000).SelectMany(startLine =>
            Gen.Choose(0, 1000).SelectMany(charOffset =>
                Gen.Choose(int.MinValue / 2, 0).Select(charCount =>
                    (startLine, charOffset, charCount))));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates (startLine, characterOffset, characterCount) where multiple params are invalid.
    /// Used to verify first-invalid-param-wins ordering (startLine checked first).
    /// </summary>
    private static Arbitrary<(int startLine, int characterOffset, int characterCount)> AllInvalidArb()
    {
        var gen = Gen.Choose(int.MinValue / 2, -1).SelectMany(startLine =>
            Gen.Choose(int.MinValue / 2, -1).SelectMany(charOffset =>
                Gen.Choose(int.MinValue / 2, 0).Select(charCount =>
                    (startLine, charOffset, charCount))));
        return Arb.From(gen);
    }

    /// <summary>
    /// When startLine is negative, GetWrappedViewAsync returns a Failure with
    /// error message "ERROR: startLine out of range".
    ///
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property NegativeStartLine_ReturnsStartLineError()
    {
        return Prop.ForAll(
            NegativeStartLineArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetWrappedViewAsync(
                    args.startLine, args.characterOffset, args.characterCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter &&
                        result.Error.Message == "ERROR: startLine out of range")
                    .Label($"startLine={args.startLine} should produce 'ERROR: startLine out of range'");
            });
    }

    /// <summary>
    /// When characterOffset is negative (and startLine is valid),
    /// GetWrappedViewAsync returns a Failure with error message
    /// "ERROR: characterOffset out of range".
    ///
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property NegativeCharacterOffset_ReturnsCharacterOffsetError()
    {
        return Prop.ForAll(
            NegativeCharacterOffsetArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetWrappedViewAsync(
                    args.startLine, args.characterOffset, args.characterCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter &&
                        result.Error.Message == "ERROR: characterOffset out of range")
                    .Label($"characterOffset={args.characterOffset} should produce 'ERROR: characterOffset out of range'");
            });
    }

    /// <summary>
    /// When characterCount is less than 1 (and startLine, characterOffset are valid),
    /// GetWrappedViewAsync returns a Failure with error message
    /// "ERROR: characterCount out of range".
    ///
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property InvalidCharacterCount_ReturnsCharacterCountError()
    {
        return Prop.ForAll(
            InvalidCharacterCountArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetWrappedViewAsync(
                    args.startLine, args.characterOffset, args.characterCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter &&
                        result.Error.Message == "ERROR: characterCount out of range")
                    .Label($"characterCount={args.characterCount} should produce 'ERROR: characterCount out of range'");
            });
    }

    /// <summary>
    /// When multiple parameters are invalid, the first invalid parameter wins
    /// (check order: startLine, characterOffset, characterCount).
    /// With all three invalid, startLine error is returned.
    ///
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property MultipleInvalidParams_FirstInvalidWins()
    {
        return Prop.ForAll(
            AllInvalidArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetWrappedViewAsync(
                    args.startLine, args.characterOffset, args.characterCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter &&
                        result.Error.Message == "ERROR: startLine out of range")
                    .Label($"With all params invalid (startLine={args.startLine}, charOffset={args.characterOffset}, charCount={args.characterCount}), startLine error should win");
            });
    }
}
