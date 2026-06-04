# File Index — Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

File Index feature. When a user selects a file via the Open File Dialog, the application scans the file in a single unified pass to build a compact, thread-safe index of line metadata. The scan detects encoding (BOM), identifies line endings, records byte lengths, AND computes character lengths simultaneously. Results are available immediately upon scan completion.

## Glossary

- **FileIndex**: The C# class responsible for owning the index data structure and orchestrating scanning of a single file
- **Line_Index**: The internal data structure within FileIndex that stores per-line length metadata (byte length and character length)
- **Unified_Scan**: The single scan pass that identifies line endings, records byte lengths, AND computes character lengths in one sequential file read
- **LinePair**: Value type `(ulong ByteLength, ulong CharLength)` representing both lengths for a single line
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
4. IF a file read error occurs during the Unified_Scan, or if the scan is aborted for any other reason (including user cancellation or memory limits), THEN THE FileIndex SHALL abort the scan, discard any partial line metadata, prevent any population of the Line_Index, transition ScanState to Failed or Cancelled as appropriate, and report the failure without producing a partial Line_Index

### Requirement 2: Line Ending Detection

**User Story:** As a user, I want line boundary detection to remain correct, so that line counts and byte lengths are accurate.

#### Acceptance Criteria

1. WHEN the FileIndex opens a file for scanning, THE FileIndex SHALL perform the Unified_Scan as a single sequential pass that identifies line endings and records Byte_Length per line by scanning raw bytes for LF (0x0A), CR (0x0D), and CRLF (0x0D 0x0A) delimiters, treating each as a single line boundary
2. WHEN the Unified_Scan completes, THE Line_Index SHALL contain the Byte_Length for every line in the file, where Byte_Length is the number of bytes in the line including the line-ending delimiter bytes (LF=1 byte, CR=1 byte, CRLF=2 bytes); the final line, if not terminated by a delimiter, stores only its content bytes
3. WHEN the Unified_Scan completes, THE Line_Index SHALL contain the total number of lines in the file, where a final segment of bytes not terminated by a line-ending delimiter counts as a line, and an empty file (zero bytes) yields a line count of zero
4. IF a file read error occurs during the Unified_Scan, or if the scan is aborted for any reason, THEN THE FileIndex SHALL abort the scan, clean up any partial line metadata, prevent any population of the Line_Index, and transition ScanState to Failed or Cancelled without producing a partial Line_Index

### Requirement 3: Character Length Computation (Integrated)

**User Story:** As a user, I want character lengths computed during the same read pass, so that all metrics are available immediately when the scan finishes.

#### Acceptance Criteria

1. WHEN performing the Unified_Scan, THE FileIndex SHALL decode line content using the file's detected encoding and compute Char_Length per line inline with line-ending detection, where Char_Length is the count of .NET characters (UTF-16 code units) in the decoded line content excluding line ending characters; BOM characters SHALL be excluded from Char_Length on the first line only
2. IF the Unified_Scan encounters a byte sequence that is invalid for the detected encoding, THEN THE FileIndex SHALL decode that sequence using the .NET Decoder's replacement fallback (U+FFFD) and count each replacement character as one UTF-16 code unit toward Char_Length
3. WHEN the Unified_Scan completes, THE Line_Index SHALL contain Char_Length for every line; partial results (Byte_Length without Char_Length) SHALL NOT be exposed to readers at any point

### Requirement 4: Encoding Detection

**User Story:** As a developer, I want encoding detection to remain BOM-based and happen before line scanning begins, so that the decoder is ready for the first byte of content.

#### Acceptance Criteria

1. WHEN the Unified_Scan begins, THE FileIndex SHALL detect encoding by reading up to 4 bytes from the file start and matching BOM signatures in order: UTF-32 LE (FF FE 00 00), UTF-32 BE (00 00 FE FF), UTF-8 (EF BB BF), UTF-16 LE (FF FE), UTF-16 BE (FE FF); IF no BOM matches, default to UTF-8 with BomByteLength of 0
2. THE FileIndex SHALL expose `Encoding` (type `System.Text.Encoding`) and `BomByteLength` (type `int`, values: 0, 2, 3, or 4) properties set during BOM detection before any line data is published
3. IF the file contains fewer than 4 bytes, THE FileIndex SHALL match BOM signatures using only available bytes and default to UTF-8 when no match

### Requirement 5: ScanState

**User Story:** As a developer, I want a simple scan state machine reflecting the single-pass reality, so that callers observe only meaningful states.

#### Acceptance Criteria

1. THE FileIndex SHALL expose a ScanState property with values: NotStarted, ScanInProgress, ScanComplete, Failed, or Cancelled
2. WHEN StartScanAsync is invoked, THE FileIndex SHALL transition ScanState from NotStarted to ScanInProgress before performing any file I/O
3. WHEN the Unified_Scan completes and both Byte_Length and Char_Length are populated for every line, THE FileIndex SHALL transition ScanState to ScanComplete
4. IF a scan fails, THEN THE FileIndex SHALL transition ScanState to Failed and expose an Error property in format "Failed to open {filePath}: {ExceptionType}" or "Scan failed for {filePath}: {ExceptionType}"
5. IF the CancellationToken is signalled, THEN THE FileIndex SHALL transition ScanState to Cancelled within 500 milliseconds
6. THE ScanState and Error properties SHALL be safe to read from any thread at any time without synchronization
7. THE ScanState SHALL only transition forward: NotStarted → ScanInProgress → ScanComplete (or to Failed/Cancelled from any active state); backward transitions SHALL never occur; Cancelled and Failed are terminal
8. WHEN ScanState reaches ScanComplete, THE Line_Index data SHALL be readable by any thread without additional synchronization

### Requirement 6: Abort Behavior

**User Story:** As a developer, I want abort semantics preserved — a failed or cancelled scan produces no partial Line_Index.

#### Acceptance Criteria

1. IF a file read error, user cancellation, or memory limit occurs during the Unified_Scan, THEN THE FileIndex SHALL clear any partially-written Line_Index entries so that Line_Index contains zero lines, transition ScanState to Failed or Cancelled, and populate the Error property
2. WHEN the CancellationToken is signalled, THE FileIndex SHALL stop scanning within 500 milliseconds
3. IF the FileIndex transitions to Failed or Cancelled, THEN every line entry visible in the Line_Index SHALL be fully written (no torn values observable by readers)

### Requirement 7: Thread-Safe Index Structure

**User Story:** As a developer, I want the Line_Index to remain safe for concurrent reads during and after the unified scan.

#### Acceptance Criteria

1. THE Line_Index SHALL support at least 4 simultaneous reader threads while a single writer thread is appending data, with every read returning a complete value (never torn)
2. THE Line_Index SHALL guarantee a line entry is not visible to readers until both its Byte_Length and Char_Length have been fully written; line count incremented only after all segment data committed
3. THE Line_Index SHALL store each line's Byte_Length and Char_Length as a pair within the same segment, using a single integer tier determined by Byte_Length (since Char_Length ≤ Byte_Length)
4. THE Line_Index SHALL permit only a single writer thread at any given time

### Requirement 8: Memory-Compact Storage

**User Story:** As a user, I want the index to use minimal memory with tiered segments, so that very large files remain indexable.

#### Acceptance Criteria

1. THE Line_Index SHALL use a single SegmentDirectory with segmented storage; each segment stores pairs of (Byte_Length, Char_Length) per line using one of four unsigned integer tiers: byte (0–255), ushort (0–65,535), uint (0–4,294,967,295), or ulong (>4,294,967,295)
2. THE Line_Index SHALL minimize total memory by splitting segments only when savings exceed per-segment metadata cost (9 bytes)
3. THE Line_Index SHALL support both widening and narrowing at segment boundaries based on Byte_Length values

### Requirement 9: File Opening Mode

**User Story:** As a user, I want the application to open files non-exclusively.

#### Acceptance Criteria

1. WHEN the FileIndex opens a file for scanning, THE FileIndex SHALL open in read-only mode (FileAccess.Read) with shared read-write access (FileShare.ReadWrite) without creating or truncating (FileMode.Open)
2. IF the file cannot be opened (IOException or UnauthorizedAccessException), THEN THE FileIndex SHALL skip scanning, transition to Failed, log at LogError level, AND populate Error
3. IF the file does not exist, THEN THE FileIndex SHALL skip scanning, transition to Failed, log at LogError level, AND populate Error

### Requirement 10: Backward Compatibility — Public API Surface

**User Story:** As a developer, I want existing consumers to continue working with minimal changes.

#### Acceptance Criteria

1. THE FileIndex SHALL expose `Index` (LineIndex), `Encoding`, `BomByteLength`, `State`, and `Error` with same types and thread-safety
2. THE LineIndex SHALL expose `LineCount` (int), `MaxByteLength` (ulong), `MaxCharLength` (ulong), `GetByteLength(int)` (ulong), `GetCharLength(int)` (ulong), and `GetByteOffset(int)` (ulong)
3. THE `GetCharLength(int)` SHALL return non-nullable `ulong` (both lengths always available simultaneously after unified scan)
4. THE FileIndex SHALL implement IDisposable following standard patterns
5. THE FileIndex `StartScanAsync()` SHALL return `Task<Result<ScanSummary, ScanError>>`

### Requirement 11: Byte Offset Query Correctness and Performance

**User Story:** As a user, I want line-to-byte navigation to stay accurate and responsive.

#### Acceptance Criteria

1. `GetByteOffset(lineIndex)` SHALL return exactly the cumulative sum of Byte_Length values for lines [0..lineIndex-1]
2. `GetByteOffset(0)` == 0 and `GetByteOffset(LineCount)` == total file size
3. `GetByteOffset` for large indices SHALL use segment-indexed prefix metadata, not full per-line accumulation from line 0
4. Nearby offset queries SHALL reuse segment-locality information

### Requirement 12: Scan Progress Tracking

**User Story:** As a developer, I want FileIndex to expose bytes-read progress during scan, so that the UI can display a progress bar.

#### Acceptance Criteria

1. WHILE a scan is in progress, THE FileIndex SHALL track the number of bytes read from the file stream, where bytes_read is incremented by the count returned from each ReadAsync call
2. THE FileIndex SHALL expose `TotalFileSize` (set from stream length before the scan loop begins) and `BytesRead` (current bytes read) as properties
3. THE `BytesRead` property SHALL be safe to read from any thread at any time without synchronization by using a volatile or interlocked mechanism, ensuring concurrent reads never observe a torn value
4. WHEN `StartScanAsync` completes successfully, `BytesRead` SHALL equal `TotalFileSize`

### Requirement 13: Resource Disposal

### Requirement 13: Resource Disposal

**User Story:** As a developer, I want FileIndex to cleanly release resources when disposed.

#### Acceptance Criteria

1. THE FileIndex SHALL accept a CancellationToken and an ILogger<FileIndex> at construction
2. WHEN Dispose is called, THE FileIndex SHALL release file stream and clear LineIndex memory, continuing to release remaining resources after failures
3. IF disposal fails for a resource, THE FileIndex SHALL log at Warning level and continue
4. Double Dispose SHALL complete without throwing

**User Story:** As a developer, I want FileIndex to cleanly release resources when disposed.
