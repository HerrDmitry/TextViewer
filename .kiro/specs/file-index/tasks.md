# Implementation Plan: File Index

## Overview

Implements the FileIndex service that scans a single file in two phases (Quick_Scan → Full_Scan) to build a memory-compact, thread-safe index of per-line metadata. The index stores interleaved (Byte_Length, Char_Length) pairs in variable-width integer tier segments with a sorted SegmentDirectory for O(log N) lookups. The service exposes thread-safe ScanState polling and IDisposable lifecycle. All code is C# .NET 10 in the `TextViewer.Services` namespace, tested with xUnit + FsCheck.

## Tasks

- [x] 1. Create data models and enums
  - [x] 1.1 Create ScanState enum and IntegerTier enum
    - Create `Services/ScanState.cs` with enum values: NotStarted, QuickScanInProgress, QuickScanComplete, FullScanInProgress, FullScanComplete, Failed, Cancelled
    - Create `Services/IntegerTier.cs` with enum values: Byte=1, UShort=2, UInt=4, ULong=8
    - _Requirements: 7.1_

  - [x] 1.2 Create Segment class
    - Create `Services/Segment.cs`
    - Implement constructor accepting StartLine, Count, Tier, and raw byte[] data
    - Implement `GetByteLength(int offsetWithinSegment): ulong` — reads first value in pair at offset
    - Implement `GetCharLength(int offsetWithinSegment): ulong` — reads second value in pair at offset
    - Implement `SetCharLength(int offsetWithinSegment, ulong value)` — writes second value in pair
    - Data layout: `[byteLen0, charLen0, byteLen1, charLen1, ...]` with each value using TierSize bytes
    - Access formulas: byteOffset = offset × 2 × TierSize; charOffset = (offset × 2 + 1) × TierSize
    - _Requirements: 4.4, 5.1_

  - [x] 1.3 Create SegmentDirectory class
    - Create `Services/SegmentDirectory.cs`
    - Implement `FindSegment(int lineIndex): Segment` — binary search on sorted segments by StartLine
    - Implement `Append(ReadOnlySpan<ulong> byteLengths, int startLineIndex)` — creates/extends segments with optimal tier selection and boundary decisions
    - Implement `SetCharLength(int lineIndex, ulong charLength)` — updates char-length slot in existing pair
    - Implement tier selection: selectTier(maxByteLength) → smallest tier fitting the value
    - Implement segment boundary decision: widen on tier increase; narrow only when memorySaved > 9 (metadata cost)
    - Expose `TotalLines` property
    - _Requirements: 5.1, 5.2, 5.3, 5.4_

- [x] 2. Checkpoint - Data model compilation
  - Ensure all tests pass, ask the user if questions arise.

- [x] 3. Implement LineIndex with thread-safe access
  - [x] 3.1 Create LineIndex class
    - Create `Services/LineIndex.cs`
    - Implement `LineCount` property (volatile int, atomic read)
    - Implement `GetByteLength(int lineIndex): ulong` — validates lineIndex < _lineCount, delegates to SegmentDirectory.FindSegment
    - Implement `GetCharLength(int lineIndex): ulong?` — returns null if lineIndex >= _charLengthsWrittenUpTo, otherwise reads from segment
    - Implement `GetByteOffset(int lineIndex): ulong` — sum of Byte_Lengths for lines 0..lineIndex-1
    - Implement internal `AppendByteLengths(ReadOnlySpan<ulong> byteLengths)` — holds _writeLock, appends pairs (byteLen, 0), publishes segment, then increments _lineCount
    - Implement internal `SetCharLength(int lineIndex, ulong charLength)` — Interlocked.Exchange on char slot, then increments _charLengthsWrittenUpTo
    - Implement internal `FinalizeCharLengths()` and `Clear()`
    - Thread-safety: _writeLock for writes, volatile _lineCount for visibility ordering, volatile _charLengthsWrittenUpTo for char-length null-sentinel
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 5.4, 5.5_

  - [x] 3.2 Write property test: Segment tier minimality
    - **Property 3: Segment tier minimality**
    - Generate random ulong[] arrays (0–1000 lines) with values spanning tier boundaries (0–255, 256–65535, 65536–4294967295, >4294967295)
    - Assert every segment's tier == selectTier(max Byte_Length in that segment)
    - Assert both values in every pair fit within the selected tier
    - Maximum 10 iterations
    - **Validates: Requirements 4.4, 5.1**

  - [x] 3.3 Write property test: Segment boundary optimality
    - **Property 4: Segment boundary optimality**
    - Generate random ulong[] arrays with tier-crossing patterns
    - Assert merging any two adjacent segments does NOT reduce total memory (data + metadata)
    - Assert splitting any segment further does NOT reduce total memory
    - Memory formula: segment memory = 9 + (Count × 2 × TierSize)
    - Maximum 10 iterations
    - **Validates: Requirements 5.2, 5.3**

  - [x] 3.4 Write property test: Segment directory lookup correctness
    - **Property 5: Segment directory lookup correctness**
    - Generate random LineIndex states (1–10000 lines) with pairs, random query indices
    - Assert FindSegment returns segment where StartLine ≤ lineIndex < StartLine + Count
    - Assert GetByteLength and GetCharLength return correct values for each line
    - Maximum 10 iterations
    - **Validates: Requirements 5.4**

  - [x] 3.5 Write unit tests for LineIndex and SegmentDirectory
    - Test zero-line file → no segments, LineCount == 0
    - Test single-line file → one segment with one pair
    - Test tier widening at segment boundary (e.g., byte tier → ushort tier)
    - Test tier narrowing at segment boundary (e.g., uint tier → byte tier when savings > 9)
    - Test narrowing NOT applied when savings ≤ 9 (metadata cost)
    - Test segment memory == 9 + Count × 2 × TierSize
    - Test GetByteOffset(0) == 0
    - Test GetByteOffset(N) == sum of Byte_Lengths[0..N-1]
    - Test GetCharLength returns null before Full_Scan writes
    - Test GetCharLength returns value after SetCharLength
    - Test SetCharLength writes to char slot without affecting byte slot
    - Test segment stores interleaved pairs (byteLen, charLen)
    - _Requirements: 4.3, 4.4, 5.1, 5.2, 5.3, 5.5_

- [x] 4. Checkpoint - LineIndex and segmentation tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 5. Implement FileIndex — file opening and Quick_Scan
  - [x] 5.1 Create FileIndex class with construction and file opening
    - Create `Services/FileIndex.cs`
    - Implement constructor: `FileIndex(string filePath, CancellationToken cancellationToken, ILogger<FileIndex> logger)`
    - Implement `StartScanAsync(): Task` — opens file with FileAccess.Read + FileShare.ReadWrite
    - Handle FileNotFoundException → Failed state, Error = "Failed to open {filePath}: FileNotFoundException", log Error
    - Handle UnauthorizedAccessException → Failed state, Error = "Failed to open {filePath}: UnauthorizedAccessException", log Error
    - Handle IOException → Failed state, Error = "Failed to open {filePath}: IOException", log Error
    - Expose volatile `State` property (ScanState), volatile `Error` property (string?), `Index` property (LineIndex)
    - Log scan start at Information level
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 6.1, 6.4, 6.6, 7.1, 7.4, 7.6_

  - [x] 5.2 Implement Quick_Scan phase
    - Scan raw bytes for LF (0x0A), CR (0x0D), CRLF (0x0D 0x0A) delimiters
    - Record Byte_Length per line including delimiter bytes (LF=1, CR=1, CRLF=2)
    - Final unterminated line stores content bytes only
    - Empty file (zero bytes) → 0 lines
    - Append pairs (byteLen, 0) to LineIndex via AppendByteLengths in batches
    - Transition: NotStarted → QuickScanInProgress → QuickScanComplete
    - On error: abort, clear LineIndex (no partial data), transition to Failed
    - On cancellation: stop I/O within 500ms, clear LineIndex, transition to Cancelled
    - Log phase transitions at Information level
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 6.1, 7.1, 7.2, 7.5_

  - [x] 5.3 Write property test: Quick_Scan byte-length round-trip
    - **Property 1: Quick_Scan byte-length round-trip**
    - Generate random byte arrays (0–10KB) with mixed line endings (LF/CR/CRLF, random content)
    - Write to temp file, run Quick_Scan, assert sum of all Byte_Lengths == file size
    - Assert reconstructing file by concatenating each line's bytes produces original content
    - Assert GetByteOffset(i) == sum of Byte_Lengths[0..i-1] for all i
    - Assert GetByteOffset(LineCount) == file size
    - Maximum 10 iterations
    - **Validates: Requirements 2.2, 2.3, 2.4**

  - [x] 5.4 Write property test: State machine transition validity
    - **Property 6: State machine transition validity**
    - Generate random sequences of scan events (success, I/O failure at various points, cancellation at various points)
    - Assert ScanState only transitions through valid edges per state diagram
    - Assert Failed/Cancelled reachable only from InProgress states or NotStarted (on open error)
    - Maximum 10 iterations
    - **Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5**

  - [x] 5.5 Write unit tests for FileIndex opening and Quick_Scan
    - Test FileIndex opens with FileShare.ReadWrite
    - Test FileIndex opens with FileAccess.Read
    - Test missing file → Failed state + correct Error format
    - Test access denied → Failed state + correct Error format
    - Test IOException on open → Failed state + correct Error format
    - Test Quick_Scan identifies LF line endings
    - Test Quick_Scan identifies CR line endings
    - Test Quick_Scan identifies CRLF line endings
    - Test Quick_Scan handles mixed line endings in single file
    - Test Quick_Scan Byte_Length includes delimiter bytes
    - Test Quick_Scan final unterminated line stores content bytes only
    - Test empty file → 0 lines
    - Test Quick_Scan error → LineIndex empty, no partial data
    - Test CancellationToken → state = Cancelled
    - Test ScanState transitions in correct order (happy path)
    - Test Error property format matches spec
    - Test log levels: scan start = Information, access error = Error
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 2.2, 2.3, 2.4, 2.5, 6.6, 7.1, 7.2, 7.4, 7.5_

- [x] 6. Checkpoint - Quick_Scan tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Implement FileIndex — Full_Scan and disposal
  - [x] 7.1 Implement Full_Scan phase
    - Auto-start Full_Scan after QuickScanComplete (no user action required)
    - Decode line content using file's detected encoding (StreamReader auto-detect or BOM detection)
    - Compute Char_Length per line = .NET string.Length of decoded content excluding delimiter chars and BOM
    - Use DecoderFallback.ReplacementFallback for invalid bytes (U+FFFD counted as 1 char)
    - Write char lengths via LineIndex.SetCharLength for each line
    - Transition: QuickScanComplete → FullScanInProgress → FullScanComplete
    - On error: transition to Failed, set Error = "Scan failed for {filePath}: {ExceptionType}"
    - On cancellation: stop I/O within 500ms, transition to Cancelled (Quick_Scan data preserved)
    - On memory failure: transition to Failed
    - Log phase transitions at Information level, non-access issues at Information level
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 6.1, 7.3, 7.4, 7.5, 6.6_

  - [x] 7.2 Implement IDisposable and resource cleanup
    - Implement `Dispose()`: close FileStream, clear LineIndex, log at Debug level
    - On resource release failure: log Warning, continue disposing remaining resources, never throw
    - CancellationToken signal → stop scanning within 500ms (no new I/O, state transition), resource cleanup may continue beyond 500ms
    - Dispose guarantees eventual release of all resources
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6_

  - [x] 7.3 Write property test: Full_Scan char-length correctness
    - **Property 2: Full_Scan char-length correctness**
    - Generate random strings with multi-byte chars (UTF-8 encoding), optional BOM, invalid byte sequences
    - Write to temp file, run full scan (Quick + Full)
    - Assert stored Char_Length for each line == .NET string.Length of decoded line content (excluding delimiters and BOM, using ReplacementFallback)
    - Maximum 10 iterations
    - **Validates: Requirements 3.2, 3.3, 3.4**

  - [x] 7.4 Write property test: Concurrent read safety
    - **Property 7: Concurrent read safety (no torn values)**
    - Generate random write sequences (byte lengths for pairs) + spawn multiple reader threads calling GetByteLength, GetCharLength, GetByteOffset concurrently with a writer thread
    - Assert every reader observes either the complete previous value or the complete new value — never a partially-written intermediate
    - Assert GetCharLength returns null or final value, never torn
    - Maximum 10 iterations
    - **Validates: Requirements 4.1, 4.2, 4.3**

  - [x] 7.5 Write unit tests for Full_Scan and disposal
    - Test Full_Scan starts automatically after Quick_Scan
    - Test Full_Scan with UTF-8 multi-byte chars (e.g., emoji, CJK)
    - Test Full_Scan with BOM → BOM excluded from Char_Length
    - Test Full_Scan with invalid bytes → replacement char counted as 1
    - Test Dispose releases file handle
    - Test Dispose logs at Debug level
    - Test disposal failure → log Warning, continue
    - Test CancellationToken during Full_Scan → Cancelled state, Quick_Scan data preserved
    - Test GetByteOffset(LineCount) == file size after full scan
    - Test log levels: non-access scan issue = Information
    - _Requirements: 3.1, 3.2, 3.4, 6.1, 6.2, 6.3, 6.6, 7.3, 7.5_

- [x] 8. Checkpoint - Full_Scan and disposal tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 9. Integration tests and wiring
  - [x] 9.1 Write integration tests for end-to-end scanning
    - Test scan real file end-to-end (Quick + Full) with known content
    - Test GetByteOffset matches actual file positions for real file
    - Test concurrent readers during active scan (4+ reader threads)
    - Test cancellation during Quick_Scan stops within 500ms
    - Test cancellation during Full_Scan stops within 500ms
    - Test file with 10,000+ lines completes without error
    - Test file opened with ReadWrite sharing (another process can read during scan)
    - _Requirements: 1.1, 2.2, 2.3, 3.2, 4.1, 6.1_

- [x] 10. Final checkpoint - All tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- All code is C# .NET 10 in `TextViewer.Services` namespace
- Tests use xUnit + FsCheck 3.1.0 in `TextViewer.Tests/` project
- FileIndex has zero awareness of callers/UI — exposes thread-safe fields only
- Caller responsibilities (Requirement 8) are NOT implemented here — they belong to the UI integration layer

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["1.2"] },
    { "id": 2, "tasks": ["1.3"] },
    { "id": 3, "tasks": ["3.1"] },
    { "id": 4, "tasks": ["3.2", "3.3", "3.4", "3.5"] },
    { "id": 5, "tasks": ["5.1"] },
    { "id": 6, "tasks": ["5.2"] },
    { "id": 7, "tasks": ["5.3", "5.4", "5.5"] },
    { "id": 8, "tasks": ["7.1"] },
    { "id": 9, "tasks": ["7.2"] },
    { "id": 10, "tasks": ["7.3", "7.4", "7.5"] },
    { "id": 11, "tasks": ["9.1"] }
  ]
}
```
