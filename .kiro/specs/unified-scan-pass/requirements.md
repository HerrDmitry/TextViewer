# Requirements Document

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

Unified Scan Pass feature. FileIndex currently scans files in two sequential phases (Quick_Scan → Full_Scan): first pass identifies line endings and records byte lengths, second pass re-reads the file to compute character lengths. This feature merges both phases into a single pass that computes byte lengths AND character lengths simultaneously during one sequential read of the file, eliminating the second file read entirely.

## Glossary

- **FileIndex**: The C# class responsible for owning the index data structure and orchestrating scanning of a single file
- **Line_Index**: The internal data structure within FileIndex that stores per-line length metadata (byte length and character length)
- **Unified_Scan**: The single scan pass that identifies line endings, records byte lengths, AND computes character lengths in one sequential file read
- **Byte_Length**: The length of a line measured in bytes, including the line-ending delimiter bytes (LF=1, CR=1, CRLF=2); the final line stores only content bytes if not terminated by a delimiter
- **Char_Length**: The length of a line measured in visible characters (decoded according to file encoding), excluding line ending characters and BOM
- **Status_Display**: The UI region beside the file name showing scan result metrics (line count, max byte length, max char length)

## Requirements

### Requirement 1: Single-Pass Scanning

**User Story:** As a user, I want the file to be read only once during scanning, so that scan time is halved for large files and I see complete metrics sooner.

#### Acceptance Criteria

1. WHEN a file is selected for scanning, THE FileIndex SHALL perform exactly one sequential read of the file (Unified_Scan) that detects encoding via BOM as its first operation, then computes both Byte_Length and Char_Length for every line in a single pass
2. WHEN the Unified_Scan completes successfully, THE Line_Index SHALL contain both Byte_Length and Char_Length for every line in the file, AND THE FileIndex SHALL transition ScanState directly from ScanInProgress to ScanComplete
3. THE FileIndex SHALL NOT perform a second file read pass under any circumstances; all per-line metadata SHALL be derived from the single sequential read
4. IF a file read error occurs during the Unified_Scan, or if the scan is aborted for any other reason (including user cancellation or memory limits), THEN THE FileIndex SHALL abort the scan, discard any partial line metadata and internal data structures accumulated before the error, prevent any population of the Line_Index, transition ScanState to Failed or Cancelled as appropriate, and report the failure without producing a partial Line_Index

### Requirement 2: Line Ending Detection (Preserved)

**User Story:** As a user, I want line boundary detection to remain correct after the merge, so that line counts and byte lengths are identical to the previous two-phase approach.

#### Acceptance Criteria

1. WHEN the FileIndex opens a file for scanning, THE FileIndex SHALL perform the Unified_Scan as a single sequential pass that identifies line endings and records Byte_Length per line by scanning raw bytes for LF (0x0A), CR (0x0D), and CRLF (0x0D 0x0A) delimiters, treating each as a single line boundary
2. WHEN the Unified_Scan completes, THE Line_Index SHALL contain the Byte_Length for every line in the file, where Byte_Length is the number of bytes in the line including the line-ending delimiter bytes (LF=1 byte, CR=1 byte, CRLF=2 bytes); the final line, if not terminated by a delimiter, stores only its content bytes
3. WHEN the Unified_Scan completes, THE Line_Index SHALL contain the total number of lines in the file, where a final segment of bytes not terminated by a line-ending delimiter counts as a line, and an empty file (zero bytes) yields a line count of zero
4. IF a file read error occurs during the Unified_Scan, or if the scan is aborted for any reason (including cancellation token signalling or memory exhaustion), THEN THE FileIndex SHALL abort the scan, immediately clean up any partial line metadata accumulated during scanning, prevent any population of the Line_Index, and transition ScanState to Failed or Cancelled without producing a partial Line_Index; the scan SHALL either complete successfully or fail explicitly with no intermediate states permitted

### Requirement 3: Character Length Computation (Integrated)

**User Story:** As a user, I want character lengths computed during the same read pass, so that all metrics are available immediately when the scan finishes.

#### Acceptance Criteria

1. WHEN performing the Unified_Scan, THE FileIndex SHALL decode line content using the file's detected encoding and compute Char_Length per line inline with line-ending detection, where Char_Length is the count of .NET characters (UTF-16 code units) in the decoded line content excluding line ending characters; BOM characters SHALL be excluded from Char_Length on the first line only (the line containing the BOM bytes)
2. IF the Unified_Scan encounters a byte sequence that is invalid for the detected encoding, THEN THE FileIndex SHALL decode that sequence using the .NET Decoder's replacement fallback (one U+FFFD per invalid byte sequence) and count each replacement character as one UTF-16 code unit toward Char_Length
3. WHEN the Unified_Scan completes, THE Line_Index SHALL contain Char_Length for every line; partial results (Byte_Length without Char_Length) SHALL NOT be exposed to readers at any point during or after the scan

### Requirement 4: Encoding Detection (Preserved)

**User Story:** As a developer, I want encoding detection to remain BOM-based and happen before line scanning begins, so that the decoder is ready for the first byte of content.

#### Acceptance Criteria

1. WHEN the Unified_Scan begins, THE FileIndex SHALL detect encoding by reading up to 4 bytes from the file start and matching BOM signatures in this order: UTF-32 LE (FF FE 00 00), UTF-32 BE (00 00 FE FF), UTF-8 (EF BB BF), UTF-16 LE (FF FE), UTF-16 BE (FE FF); IF no BOM matches, THEN THE FileIndex SHALL default to UTF-8 with BomByteLength of 0
2. THE FileIndex SHALL expose `Encoding` (type `System.Text.Encoding`) and `BomByteLength` (type `int`, values: 0, 2, 3, or 4) properties that are set during BOM detection and before any line data is published to the Line_Index; both properties SHALL never be null once scanning has started
3. IF the file contains fewer than 4 bytes, THEN THE FileIndex SHALL match BOM signatures using only the available bytes (2-byte or 3-byte BOMs may still match) and default to UTF-8 when no available bytes match any BOM prefix

### Requirement 5: ScanState Simplification

**User Story:** As a developer, I want the scan state machine simplified to reflect the single-pass reality, so that callers do not observe intermediate states that no longer exist.

#### Acceptance Criteria

1. THE FileIndex SHALL expose a ScanState property with values: NotStarted, ScanInProgress, ScanComplete, Failed, or Cancelled
2. WHEN StartScanAsync is invoked, THE FileIndex SHALL transition ScanState from NotStarted to ScanInProgress before performing any file I/O
3. WHEN the Unified_Scan completes and both Byte_Length and Char_Length are populated for every line, THE FileIndex SHALL transition ScanState to ScanComplete
4. IF a scan fails due to a file access error or an unrecoverable read/decode error, THEN THE FileIndex SHALL transition ScanState to Failed and expose an Error property containing the failure reason in the format "Failed to open {filePath}: {ExceptionType}" or "Scan failed for {filePath}: {ExceptionType}"
5. IF the CancellationToken is signalled during scanning, THEN THE FileIndex SHALL transition ScanState to Cancelled within 500 milliseconds of the signal
6. THE ScanState and Error properties SHALL be safe to read from any thread at any time without synchronization by the caller
7. THE ScanState SHALL only transition forward through the sequence NotStarted → ScanInProgress → ScanComplete (or to Failed or Cancelled from any active state); backward transitions SHALL never occur; Cancelled and Failed SHALL be terminal states that prevent further scanning on the same FileIndex instance
8. WHEN ScanState reaches ScanComplete, THE Line_Index data (Byte_Length and Char_Length for every line) SHALL be readable by any thread without additional synchronization

### Requirement 6: Abort Behavior (Preserved)

**User Story:** As a developer, I want abort semantics preserved — a failed or cancelled scan produces no partial Line_Index, so that consumers never observe incomplete data.

#### Acceptance Criteria

1. IF a file read error, user cancellation, or memory limit occurs during the Unified_Scan, THEN THE FileIndex SHALL abort the scan, clear any partially-written Line_Index entries so that Line_Index contains zero lines, transition ScanState to Failed or Cancelled, and populate the Error property with the failure reason
2. WHEN the CancellationToken is signalled, THE FileIndex SHALL stop scanning within 500 milliseconds, where "stop scanning" means: no new file I/O operations are issued AND ScanState has transitioned to Cancelled within 500ms; IF the 500ms deadline cannot be met due to blocking I/O, THEN THE FileIndex SHALL transition ScanState to Failed instead
3. IF the FileIndex transitions ScanState to Failed or Cancelled during the scan, THEN THE FileIndex SHALL guarantee that every line entry visible in the Line_Index is fully written (no torn or intermediate values are observable by reader threads)

### Requirement 7: Thread-Safe Index Structure (Preserved)

**User Story:** As a developer, I want the Line_Index to remain safe for concurrent reads during and after the unified scan, so that multiple consumers can query without synchronization.

#### Acceptance Criteria

1. WHEN concurrent read access occurs, THE Line_Index SHALL support at least 4 simultaneous reader threads while a single writer thread is appending data, such that every read returns a complete, previously-written value and never a torn or partially-updated value
2. THE Line_Index SHALL guarantee at all times that a line entry is not visible to readers until both its Byte_Length and Char_Length have been fully written to the segment; specifically, the line count SHALL only be incremented after all segment data for the appended batch is committed
3. THE Line_Index SHALL store each line's Byte_Length and Char_Length as a pair within the same segment, using a single integer tier determined by the larger of the two values (Byte_Length, since it includes delimiter bytes and Char_Length is always ≤ Byte_Length); both values in a pair use the same tier width
4. THE Line_Index SHALL permit only a single writer thread to modify the index at any given time; concurrent writes from multiple threads are not supported

### Requirement 8: Memory-Compact Storage (Preserved)

**User Story:** As a user, I want the index to continue using minimal memory with tiered segments, so that very large files remain indexable without excessive consumption.

#### Acceptance Criteria

1. THE Line_Index SHALL use a single SegmentDirectory with a segmented storage strategy where consecutive lines are grouped into variable-length segments; each segment stores pairs of (Byte_Length, Char_Length) per line using one of four unsigned integer tiers: byte (0–255), ushort (0–65,535), uint (0–4,294,967,295), or ulong (>4,294,967,295)
2. THE Line_Index SHALL minimize total memory consumption when deciding segment boundaries, such that a segment is only split when the memory saved by using a narrower type exceeds the additional per-segment metadata cost
3. THE Line_Index SHALL support both widening (transitioning to a wider tier) and narrowing (transitioning to a narrower tier) at segment boundaries based on line Byte_Length values

### Requirement 9: Caller State Observation (Updated)

**User Story:** As a user, I want to see scan results when the single pass completes and error messages when scans fail, so that I get immediate feedback.

#### Acceptance Criteria

1. WHEN the caller observes ScanState = ScanComplete, THE Status_Display SHALL show the total number of lines, the longest Byte_Length, and the longest Char_Length beside the file name
2. WHILE the caller observes ScanState = ScanInProgress, THE Status_Display SHALL display a visible scanning indicator element beside the file name
3. IF the caller observes ScanState = Failed or Cancelled, THEN THE Status_Display SHALL not display metrics from the failed or cancelled scan and SHALL revert to the state prior to the failed scan
4. IF the caller observes ScanState = Failed, THEN THE Status_Display SHALL display the FileIndex Error field (a non-empty string describing the failure reason) in the main content area, replacing any previously displayed file content

### Requirement 10: Backward Compatibility — Public API Surface

**User Story:** As a developer, I want existing consumers (FileViewService, Message Bus handlers) to continue working with minimal changes, so that the refactor does not cascade into unrelated code.

#### Acceptance Criteria

1. THE FileIndex SHALL continue to expose `Index` (LineIndex), `Encoding`, `BomByteLength`, `State`, and `Error` properties with the same types (LineIndex, System.Text.Encoding, int, ScanState, string?) and the same thread-safety mechanisms (volatile reads for State and Error, immutable-after-init for Encoding and BomByteLength)
2. THE LineIndex SHALL continue to expose `LineCount` (int), `MaxByteLength` (ulong), `MaxCharLength` (ulong), `GetByteLength(int)` (returns ulong), `GetCharLength(int)` (returns ulong), and `GetByteOffset(int)` (returns ulong) as public members with identical signatures and observable behavior
3. THE `GetCharLength(int)` method SHALL return `ulong` (non-nullable) once ScanState reaches ScanComplete, since both lengths are always available simultaneously after the unified scan
4. THE FileIndex SHALL continue to implement IDisposable, releasing the file handle, internal buffers, and index memory only when Dispose is explicitly called (following standard IDisposable patterns with no finalizer or background cleanup); resource-release failures SHALL be logged via ILogger without throwing exceptions
5. THE FileIndex `StartScanAsync()` method SHALL continue to return `Task<Result<ScanSummary, ScanError>>` with the same ScanSummary and ScanError record types

### Requirement 11: File Opening Mode (Preserved)

**User Story:** As a user, I want the application to continue opening files non-exclusively, so that other processes can continue reading or writing.

#### Acceptance Criteria

1. WHEN the FileIndex opens a file for scanning, THE FileIndex SHALL open the file in read-only mode (FileAccess.Read) with shared read-write access (FileShare.ReadWrite) without creating or truncating the file (FileMode.Open)
2. IF the file cannot be opened due to an access error (IOException or UnauthorizedAccessException), THEN THE FileIndex SHALL skip scanning, transition State to Failed, log at LogError level, AND populate the Error property with a message indicating the exception type
3. IF the file does not exist at the specified path (FileNotFoundException), THEN THE FileIndex SHALL skip scanning, transition State to Failed, log at LogError level, AND populate the Error property with a message indicating the file was not found

### Requirement 12: Resource Disposal (Preserved)

**User Story:** As a developer, I want FileIndex to cleanly release resources when disposed, so that the caller can manage FileIndex lifetime without leaks.

#### Acceptance Criteria

1. THE FileIndex SHALL accept a CancellationToken and an ILogger<FileIndex> at construction
2. WHEN Dispose is called, THE FileIndex SHALL release the file stream and clear the LineIndex memory, continuing to attempt release of remaining resources even after catastrophic failures (e.g. OutOfMemoryException) in prior releases
3. IF the FileIndex fails to release a resource during disposal, THEN THE FileIndex SHALL log the failure at Warning level and continue disposing remaining resources; exceptions MAY be thrown after disposal completes if internal failures warrant it
4. IF Dispose is called more than once, THEN THE FileIndex SHALL complete without throwing an exception and without attempting to release already-released resources
