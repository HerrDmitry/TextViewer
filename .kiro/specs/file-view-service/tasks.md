# Implementation Plan: File View Service

## Overview

Implement a C# backend service (`FileViewService`) that produces rectangular text views from an indexed file. The service owns a private `FileIndex`, opens independent file handles per request for concurrent safety, decodes bytes using the detected encoding, and returns row strings via a Result pattern. Implementation builds incrementally: data types first, then FileIndex additions, core extraction logic, error handling, and finally integration wiring.

## Tasks

- [x] 1. Create data types and Result pattern
  - [x] 1.1 Create `Result<T, E>` struct in `Services/Result.cs`
    - Implement discriminated union with `IsSuccess`, `Value`, `Error` properties
    - Add static factory methods `Success(T)` and `Failure(E)`
    - _Requirements: 7.1_

  - [x] 1.2 Create `ViewErrorCode` enum and `ViewError` class in `Services/ViewError.cs`
    - Define enum values: InvalidParameter, FileNotAccessible, IoError, Cancelled
    - Note: Cancelled is reserved — not emitted by current GetViewAsync; documented for future non-throwing cancellation API
    - Implement `ViewError` with `Code` and `Message` properties
    - _Requirements: 7.5_

  - [x] 1.3 Create `ViewResult` class in `Services/ViewResult.cs`
    - Implement with `IReadOnlyList<string> Rows` property
    - Constructor accepts `IReadOnlyList<string>`
    - _Requirements: 1.1_

- [x] 2. Add Encoding exposure to FileIndex
  - [x] 2.1 Add `Encoding` and `BomByteLength` public properties to `FileIndex`
    - Add `public Encoding Encoding { get; private set; } = Encoding.UTF8;`
    - Add `public int BomByteLength { get; private set; } = 0;`
    - Set both properties inside `DetectEncodingAsync()` before returning
    - Ensure properties are available immediately after scan starts (set before line indexing begins)
    - _Requirements: 3.1, 3.2, 3.3_

  - [x] 2.2 Write unit tests for FileIndex Encoding properties
    - Test UTF-8 BOM detection sets Encoding and BomByteLength=3
    - Test UTF-16 LE BOM sets Encoding and BomByteLength=2
    - Test UTF-16 BE BOM sets Encoding and BomByteLength=2
    - Test UTF-32 LE BOM sets Encoding and BomByteLength=4
    - Test UTF-32 BE BOM sets Encoding and BomByteLength=4
    - Test no BOM defaults to UTF-8 and BomByteLength=0
    - _Requirements: 3.1, 3.2, 3.3_

- [x] 3. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Implement FileViewService core
  - [x] 4.1 Create `FileViewService` class in `Services/FileViewService.cs`
    - Implement `IDisposable`
    - Constructor accepts `string filePath`, `CancellationToken cancellationToken`, `ILogger<FileViewService> logger`
    - Create private `FileIndex` instance, call `StartScanAsync()` (fire-and-forget or via init method)
    - Expose `ScanState` property delegating to `_fileIndex.State`
    - Implement `Dispose()` to dispose the FileIndex
    - _Requirements: 2.1, 2.2, 2.4, 2.5_

  - [x] 4.2 Implement input validation in `GetViewAsync`
    - Validate startLine >= 0, startCol >= 0, rowCount >= 1, colCount >= 1
    - Return `Failure(InvalidParameter, ...)` with descriptive message for first invalid parameter
    - Validation must occur before any file I/O or FileIndex lookup
    - _Requirements: 1.8, 4.1, 4.2, 4.3, 4.4, 4.5_

  - [x] 4.3 Write property test for input validation (Property 3)
    - **Property 3: Invalid parameters rejected before I/O**
    - Generate random negative/zero values for each parameter
    - Assert ViewError with InvalidParameter code returned, no file I/O performed
    - Use `[Property(MaxTest = 10)]`
    - **Validates: Requirements 1.8, 4.1, 4.2, 4.3, 4.4, 4.5**

  - [x] 4.4b Write unit test for request during QuickScan → partial + empty rows
    - Create a FileViewService with a file being scanned (mock or slow scan so lines beyond range are unscanned)
    - Request rows beyond the currently scanned range
    - Assert partial results with empty strings for unscanned lines
    - _Requirements: 2.3, 1.5_

  - [x] 4.4 Implement edge case handling in `GetViewAsync`
    - After validation, check cancellation token
    - Snapshot `_fileIndex.Index.LineCount` (volatile read) and `_fileIndex.State`
    - If scan complete and 0 lines: return `Success([""])`
    - If scan complete and startLine >= lineCount: return `Success([""])`
    - _Requirements: 1.6, 1.7_

  - [x] 4.5 Implement row extraction loop in `GetViewAsync`
    - Open independent `FileStream` with `FileMode.Open`, `FileAccess.Read`, `FileShare.ReadWrite`
    - Loop over rowCount rows starting at startLine
    - For scan-complete and lineIdx >= scannedLines: break (fewer rows returned)
    - For scan-in-progress and lineIdx >= scannedLines: append empty string, continue
    - Get byte offset and byte length from FileIndex for each line
    - Seek and read line bytes
    - Ensure non-empty result (if rows empty, add single empty string)
    - Dispose file handle in finally block
    - _Requirements: 1.1, 1.4, 1.5, 6.2_

  - [x] 4.6 Write property test for result count invariant (Property 2)
    - **Property 2: Result count invariant**
    - Generate random line counts (0–100), random startLine/rowCount spanning/exceeding file
    - Assert count == min(R, max(0, N-S)) or 1 for edge cases
    - Use `[Property(MaxTest = 10)]`
    - **Validates: Requirements 1.4, 1.5, 1.6, 1.7**

- [x] 5. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 6. Implement character decoding
  - [x] 6.1 Implement partial decode algorithm (`DecodeUpTo` helper method)
    - Use `_fileIndex.Encoding` and `_fileIndex.BomByteLength` for decoding
    - Detect delimiter bytes at end of line (CRLF=2, LF=1, CR=1, none=0)
    - Skip BOM bytes on first line
    - Use streaming `Decoder` with `DecoderReplacementFallback` for invalid bytes → U+FFFD
    - Decode only up to `startCol + colCount` characters (partial decode for performance)
    - Return (content string, delimiter string)
    - _Requirements: 5.1, 5.2, 5.3, 5.5_

  - [x] 6.2 Integrate decode into row extraction
    - After reading line bytes, call decode helper
    - Slice content: if startCol >= content.Length → return delimiter only
    - Otherwise return `content[startCol..min(startCol+colCount, content.Length)] + delimiter`
    - Column = .NET char (UTF-16 code unit); surrogates = 2 columns each
    - Delimiters appended but not counted toward colCount
    - _Requirements: 1.2, 1.3, 5.3, 5.4_

  - [x] 6.3 Write property test for row extraction correctness (Property 1)
    - **Property 1: Row extraction correctness**
    - Generate random byte content (0–4KB) with mixed encodings and line endings
    - Assert extracted row == independent full-file decode + slice of same region
    - Use `[Property(MaxTest = 10)]`
    - **Validates: Requirements 1.2, 5.1, 5.5**

  - [x] 6.4 Write property test for invalid byte replacement (Property 4)
    - **Property 4: Invalid byte replacement**
    - Generate random byte arrays with injected invalid sequences per encoding
    - Assert U+FFFD present for invalid sequences, each counts as 1 column
    - Use `[Property(MaxTest = 10)]`
    - **Validates: Requirements 5.2**

  - [x] 6.5 Write property test for column counting (Property 5)
    - **Property 5: Column counting — code units counted, delimiters excluded**
    - Generate random strings with surrogates, control chars, various delimiters
    - Assert content length ≤ colCount code units; delimiter appended outside budget
    - Use `[Property(MaxTest = 10)]`
    - **Validates: Requirements 5.3, 5.4**

- [x] 7. Implement error handling and cancellation
  - [x] 7.1 Add error handling to `GetViewAsync`
    - Catch `FileNotFoundException` → `Failure(FileNotAccessible, ...)`
    - Catch `UnauthorizedAccessException` → `Failure(IoError, ...)` — Note: UnauthorizedAccessException maps to IoError (not FileNotAccessible) per design normative table
    - Catch `IOException` → `Failure(IoError, ...)`
    - Check if FileIndex is in Failed state before opening handle → `Failure(FileNotAccessible, ...)`
    - _Requirements: 7.1, 7.2, 7.3_

  - [x] 7.2 Add cancellation support to `GetViewAsync`
    - Link service-level and per-request CancellationTokens
    - Check cancellation before extraction and between row reads
    - Throw `OperationCanceledException` when cancelled
    - _Requirements: 2.6, 7.4_

  - [x] 7.3 Write unit tests for error handling
    - Test IOException → IoError result
    - Test FileNotFoundException → FileNotAccessible result
    - Test cancellation → OperationCanceledException
    - Test ViewError has correct code + message
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5_

- [x] 8. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 9. Concurrent access and FileIndex immutability
  - [x] 9.1 Verify concurrent safety implementation
    - Ensure independent FileStream per request (already in 4.5)
    - Ensure FileAccess.Read only, FileShare.ReadWrite
    - Ensure no writes to FileIndex during extraction (read-only queries only)
    - _Requirements: 6.1, 6.2, 6.3_

  - [x] 9.1b Write unit test asserting FileStream opened with FileAccess.Read and FileShare.ReadWrite
    - Verify the file handle is opened with exactly FileAccess.Read (not Write or ReadWrite)
    - Verify the file handle is opened with FileShare.ReadWrite
    - Use mock or wrapper to intercept FileStream construction and assert parameters
    - _Requirements: 6.2_

  - [x] 9.2 Write property test for FileIndex immutability (Property 6)
    - **Property 6: FileIndex immutability during extraction**
    - Snapshot LineCount and byte lengths before request, verify unchanged after
    - Use `[Property(MaxTest = 10)]`
    - **Validates: Requirements 6.3**

  - [x] 9.3 Write integration test for concurrent access
    - Issue 4 concurrent GetViewAsync requests against same FileViewService
    - Assert all produce correct, independent results
    - _Requirements: 6.1_

- [x] 10. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- The design specifies C# with .NET 10, xUnit, and FsCheck for property-based tests
- All property tests use `[Property(MaxTest = 10)]` per workspace testing policy
- FileIndex already exists — only the `Encoding`/`BomByteLength` properties need to be added
- `ScanState` enum already exists in `Services/ScanState.cs`

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2", "1.3"] },
    { "id": 1, "tasks": ["2.1"] },
    { "id": 2, "tasks": ["2.2", "4.1"] },
    { "id": 3, "tasks": ["4.2", "4.4", "4.4b"] },
    { "id": 4, "tasks": ["4.3", "4.5"] },
    { "id": 5, "tasks": ["4.6", "6.1"] },
    { "id": 6, "tasks": ["6.2"] },
    { "id": 7, "tasks": ["6.3", "6.4", "6.5", "7.1"] },
    { "id": 8, "tasks": ["7.2"] },
    { "id": 9, "tasks": ["7.3", "9.1"] },
    { "id": 10, "tasks": ["9.1b", "9.2", "9.3"] }
  ]
}
```
