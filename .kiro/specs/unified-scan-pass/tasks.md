# Implementation Plan: Unified Scan Pass

## Overview

Merge FileIndex's two-phase scan (Quick_Scan → Full_Scan) into a single unified pass that computes byte lengths AND char lengths simultaneously. Simplify ScanState enum, replace `AppendByteLengths` + `SetCharLength` with atomic `AppendLinePairs`, and remove progressive char-length availability.

## Tasks

- [x] 1. Simplify ScanState enum and add LinePair value type
  - [x] 1.1 Replace ScanState enum with unified states (NotStarted, ScanInProgress, ScanComplete, Failed, Cancelled)
    - Remove `QuickScanInProgress`, `QuickScanComplete`, `FullScanInProgress`, `FullScanComplete`
    - Add `ScanInProgress = 1`, `ScanComplete = 2`, `Failed = 3`, `Cancelled = 4`
    - File: `Services/ScanState.cs`
    - _Requirements: 5.1, 5.7_

  - [x] 1.2 Create LinePair readonly record struct
    - Add `internal readonly record struct LinePair(ulong ByteLength, ulong CharLength)` in new file or `ScanState.cs`
    - File: `Services/LinePair.cs` (new)
    - _Requirements: 3.3, 7.3_

- [x] 2. Update LineIndex to use AppendLinePairs
  - [x] 2.1 Replace `AppendByteLengths` and `SetCharLength` with `AppendLinePairs(ReadOnlySpan<LinePair>)`
    - Remove `_charLengthsWrittenUpTo` field entirely
    - Remove `FinalizeCharLengths()` method
    - Change `MaxCharLength` from `ulong?` to `ulong` (non-nullable)
    - Change `GetCharLength` return type from `ulong?` to `ulong`
    - In `AppendLinePairs`: determine tier from max byte length in batch, write both values atomically per pair, update `_maxCharLength` alongside `_maxByteLength`, increment `_lineCount` only after all segment data committed
    - File: `Services/LineIndex.cs`
    - _Requirements: 3.3, 7.1, 7.2, 7.3, 10.2, 10.3_

  - [x] 2.2 Write property test: Concurrent read safety (Property 5)
    - **Property 5: Concurrent read safety**
    - **Validates: Requirements 7.1, 7.2, 3.3**

  - [x] 2.3 Write property test: Segment tier minimality (Property 6)
    - **Property 6: Segment tier minimality**
    - **Validates: Requirements 7.3, 8.1**

- [x] 3. Update SegmentDirectory to accept LinePair spans
  - [x] 3.1 Replace `Append(ReadOnlySpan<ulong>, int)` with `Append(ReadOnlySpan<LinePair>, int)`
    - Tier selection uses `max(byteLength)` per segment run (charLength ≤ byteLength guarantees fit)
    - Write both byteLength and charLength into segment data in one allocation
    - Remove `SetCharLength` method from SegmentDirectory
    - File: `Services/SegmentDirectory.cs`
    - _Requirements: 8.1, 8.2, 8.3_

  - [x] 3.2 Write property test: Segment boundary optimality (Property 7)
    - **Property 7: Segment boundary optimality**
    - **Validates: Requirements 8.2, 8.3**

- [x] 4. Checkpoint
  - Ensure all tests pass, ask the user if questions arise.

- [x] 5. Rewrite FileIndex to unified single-pass scan
  - [x] 5.1 Rewrite `StartScanAsync` with unified state transitions
    - Remove Quick_Scan / Full_Scan separation
    - Transition: NotStarted → ScanInProgress → ScanComplete (or Failed/Cancelled)
    - Open file, call `RunUnifiedScanAsync()`, handle errors with same patterns
    - Remove `FinalizeCharLengths()` call
    - File: `Services/FileIndex.cs`
    - _Requirements: 1.1, 1.2, 1.3, 5.2, 5.3, 5.4, 5.5, 11.1, 11.2, 11.3_

  - [x] 5.2 Implement `RunUnifiedScanAsync` — BOM detection + unified scan loop
    - Step 1: DetectBom (read up to 4 bytes, set Encoding + BomByteLength)
    - Step 2: Create Decoder with ReplacementFallback from detected encoding
    - Step 3: Seek to post-BOM position (or remain at current position if BOM bytes consumed)
    - Step 4: Sequential read loop — for each byte: detect line endings, accumulate line bytes, on line boundary decode content bytes → charLength, emit LinePair, batch and flush via `AppendLinePairs`
    - Step 5: Flush final line + remaining batch
    - BOM chars excluded from first line's charLength
    - Invalid bytes → U+FFFD counted as 1 code unit
    - File: `Services/FileIndex.cs`
    - _Requirements: 1.1, 2.1, 2.2, 2.3, 3.1, 3.2, 4.1, 4.2, 4.3_

  - [x] 5.3 Implement abort/cancellation semantics in unified scan
    - Check `_cancellationToken.ThrowIfCancellationRequested()` between buffer reads
    - On any exception (IOException, OOM, OperationCanceledException): clear LineIndex, set state
    - Ensure no partial Line_Index exposed on failure
    - File: `Services/FileIndex.cs`
    - _Requirements: 1.4, 6.1, 6.2, 6.3_

  - [x] 5.4 Write property test: Byte-length round-trip (Property 1)
    - **Property 1: Byte-length round-trip**
    - **Validates: Requirements 1.1, 2.1, 2.2, 2.3**

  - [x] 5.5 Write property test: Char-length correctness (Property 2)
    - **Property 2: Char-length correctness**
    - **Validates: Requirements 3.1, 3.2**

  - [x] 5.6 Write property test: Abort produces no partial index (Property 3)
    - **Property 3: Abort produces no partial index**
    - **Validates: Requirements 1.4, 2.4, 6.1, 6.3**

  - [x] 5.7 Write property test: State machine transition validity (Property 4)
    - **Property 4: State machine transition validity**
    - **Validates: Requirements 5.7**

- [x] 6. Checkpoint
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Update callers and remove dead code
  - [x] 7.1 Update FileViewService and Program.cs handler references to new ScanState values
    - Replace `ScanState.QuickScanComplete`, `FullScanComplete` → `ScanState.ScanComplete`
    - Replace `ScanState.QuickScanInProgress`, `FullScanInProgress` → `ScanState.ScanInProgress`
    - Remove references to `MaxCharLength` nullable handling (now non-nullable `ulong`)
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5_

  - [x] 7.2 Remove dead two-phase methods from LineIndex
    - Remove `AppendByteLengths`, `SetCharLength`, `FinalizeCharLengths`, `_charLengthsWrittenUpTo`
    - Verify no remaining references
    - File: `Services/LineIndex.cs`
    - _Requirements: 1.3_

  - [x] 7.3 Remove `SetCharLength` from Segment and SegmentDirectory
    - Delete `Segment.SetCharLength` method
    - Delete `SegmentDirectory.SetCharLength` method
    - File: `Services/Segment.cs`, `Services/SegmentDirectory.cs`
    - _Requirements: 1.3_

- [x] 8. Update existing tests for new API
  - [x] 8.1 Update existing xUnit tests to use new ScanState values and non-nullable MaxCharLength
    - Fix all test references to removed states (QuickScanInProgress, QuickScanComplete, FullScanInProgress, FullScanComplete)
    - Fix all nullable MaxCharLength assertions → non-nullable
    - Update any tests calling `AppendByteLengths` / `SetCharLength` → `AppendLinePairs`
    - _Requirements: 10.2, 10.3_

  - [x] 8.2 Write unit tests for unified scan edge cases
    - Empty file → 0 lines
    - LF/CR/CRLF/mixed line endings
    - UTF-8 multi-byte chars + BOM exclusion
    - Invalid bytes → U+FFFD
    - File < 4 bytes BOM detection
    - _Requirements: 2.1, 2.2, 2.3, 3.1, 3.2, 4.1, 4.3_

- [x] 9. Final checkpoint
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from design document
- Unit tests validate specific examples and edge cases
- C# / .NET 10 with FsCheck 2.x + xUnit for property-based tests (MaxTest = 10 per workspace policy)

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2"] },
    { "id": 1, "tasks": ["2.1", "3.1"] },
    { "id": 2, "tasks": ["2.2", "2.3", "3.2"] },
    { "id": 3, "tasks": ["5.1", "5.2", "5.3"] },
    { "id": 4, "tasks": ["5.4", "5.5", "5.6", "5.7"] },
    { "id": 5, "tasks": ["7.1", "7.2", "7.3"] },
    { "id": 6, "tasks": ["8.1", "8.2"] }
  ]
}
```
