using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property 3: Invalid parameters rejected before I/O
/// For any view request where startLine &lt; 0, startCol &lt; 0, rowCount &lt; 1, or colCount &lt; 1,
/// the service SHALL return a ViewError with code InvalidParameter without opening any file handle
/// or querying the FileIndex for line data.
///
/// **Validates: Requirements 1.8, 4.1, 4.2, 4.3, 4.4, 4.5**
/// </summary>
public class FileViewServiceValidationPropertyTests
{
    /// <summary>
    /// Uses a non-existent file path. If validation rejects before I/O,
    /// the service will never attempt to open the file, so a non-existent path
    /// should still return InvalidParameter (not FileNotAccessible).
    /// </summary>
    private const string NonExistentPath = @"C:\__does_not_exist__\no_file.txt";

    private static FileViewService CreateService()
    {
        var logger = NullLogger<FileViewService>.Instance;
        return new FileViewService(NonExistentPath, CancellationToken.None, logger);
    }

    /// <summary>
    /// Generates a tuple of (startLine, startCol, rowCount, colCount) where startLine is negative.
    /// Other parameters are valid.
    /// </summary>
    private static Arbitrary<(int startLine, int startCol, int rowCount, int colCount)> NegativeStartLineArb()
    {
        var gen = Gen.Choose(int.MinValue / 2, -1).SelectMany(startLine =>
            Gen.Choose(0, 1000).SelectMany(startCol =>
                Gen.Choose(1, 100).SelectMany(rowCount =>
                    Gen.Choose(1, 200).Select(colCount =>
                        (startLine, startCol, rowCount, colCount)))));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a tuple where startCol is negative. Other parameters are valid.
    /// </summary>
    private static Arbitrary<(int startLine, int startCol, int rowCount, int colCount)> NegativeStartColArb()
    {
        var gen = Gen.Choose(0, 1000).SelectMany(startLine =>
            Gen.Choose(int.MinValue / 2, -1).SelectMany(startCol =>
                Gen.Choose(1, 100).SelectMany(rowCount =>
                    Gen.Choose(1, 200).Select(colCount =>
                        (startLine, startCol, rowCount, colCount)))));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a tuple where rowCount is less than 1. Other parameters are valid.
    /// </summary>
    private static Arbitrary<(int startLine, int startCol, int rowCount, int colCount)> InvalidRowCountArb()
    {
        var gen = Gen.Choose(0, 1000).SelectMany(startLine =>
            Gen.Choose(0, 1000).SelectMany(startCol =>
                Gen.Choose(int.MinValue / 2, 0).SelectMany(rowCount =>
                    Gen.Choose(1, 200).Select(colCount =>
                        (startLine, startCol, rowCount, colCount)))));
        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a tuple where colCount is less than 1. Other parameters are valid.
    /// </summary>
    private static Arbitrary<(int startLine, int startCol, int rowCount, int colCount)> InvalidColCountArb()
    {
        var gen = Gen.Choose(0, 1000).SelectMany(startLine =>
            Gen.Choose(0, 1000).SelectMany(startCol =>
                Gen.Choose(1, 100).SelectMany(rowCount =>
                    Gen.Choose(int.MinValue / 2, 0).Select(colCount =>
                        (startLine, startCol, rowCount, colCount)))));
        return Arb.From(gen);
    }

    /// <summary>
    /// When startLine is negative, GetViewAsync returns InvalidParameter error
    /// without performing any file I/O (non-existent path does not cause FileNotAccessible).
    ///
    /// **Validates: Requirements 1.8, 4.1, 4.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property NegativeStartLine_ReturnsInvalidParameter()
    {
        return Prop.ForAll(
            NegativeStartLineArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetViewAsync(args.startLine, args.startCol, args.rowCount, args.colCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter)
                    .Label($"startLine={args.startLine} should be rejected as InvalidParameter");
            });
    }

    /// <summary>
    /// When startCol is negative, GetViewAsync returns InvalidParameter error
    /// without performing any file I/O.
    ///
    /// **Validates: Requirements 1.8, 4.2, 4.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property NegativeStartCol_ReturnsInvalidParameter()
    {
        return Prop.ForAll(
            NegativeStartColArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetViewAsync(args.startLine, args.startCol, args.rowCount, args.colCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter)
                    .Label($"startCol={args.startCol} should be rejected as InvalidParameter");
            });
    }

    /// <summary>
    /// When rowCount is less than 1, GetViewAsync returns InvalidParameter error
    /// without performing any file I/O.
    ///
    /// **Validates: Requirements 1.8, 4.3, 4.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property InvalidRowCount_ReturnsInvalidParameter()
    {
        return Prop.ForAll(
            InvalidRowCountArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetViewAsync(args.startLine, args.startCol, args.rowCount, args.colCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter)
                    .Label($"rowCount={args.rowCount} should be rejected as InvalidParameter");
            });
    }

    /// <summary>
    /// When colCount is less than 1, GetViewAsync returns InvalidParameter error
    /// without performing any file I/O.
    ///
    /// **Validates: Requirements 1.8, 4.4, 4.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property InvalidColCount_ReturnsInvalidParameter()
    {
        return Prop.ForAll(
            InvalidColCountArb(),
            args =>
            {
                using var service = CreateService();
                var result = service.GetViewAsync(args.startLine, args.startCol, args.rowCount, args.colCount).Result;
                return (!result.IsSuccess &&
                        result.Error.Code == ViewErrorCode.InvalidParameter)
                    .Label($"colCount={args.colCount} should be rejected as InvalidParameter");
            });
    }
}
