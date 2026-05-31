# File Index — Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

File Index feature. When a user selects a file via the Open File Dialog, the application scans the file in two phases to build a compact, thread-safe index of line metadata. The index tracks both byte-lengths and visible-character-lengths per line. Results are displayed progressively in the UI as each scan phase completes.

## Glossary

- **FileIndex**: The C# class responsible for owning the index data structure and orchestrating scanning of a single file
- **Line_Index**: The internal data structure within FileIndex that stores per-line length metadata (byte length and character length)
- **Quick_Scan**: The first scan phase that identifies line endings and records byte lengths per line (including delimiter bytes)
- **Full_Scan**: The second scan phase that computes visible character lengths per line using the file's encoding
- **Byte_Length**: The length of a line measured in bytes, including the line-ending delimiter bytes (LF=1, CR=1, CRLF=2); the final line stores only content bytes if not terminated by a delimiter
- **Char_Length**: The length of a line measured in visible characters (decoded according to file encoding)
- **Status_Display**: The UI region beside the file name showing scan result metrics (line count, max byte length, max char length)

## Requirements

### Requirement 1: File Opening Mode

**User Story:** As a user, I want the application to open files non-exclusively, so that other processes can continue reading or writing the file while it is being scanned.

#### Acceptance Criteria

1. WHEN the FileIndex opens a file for scanning, THE FileIndex SHALL open the file with FileShare.ReadWrite access mode
2. WHEN the FileIndex opens a file for scanning, THE FileIndex SHALL request only read access (FileAccess.Read) to the file
3. IF the file cannot be opened due to an access error (IOException or UnauthorizedAccessException), THEN THE FileIndex SHALL always skip scanning, log at LogError level a diagnostic message containing the file path and exception type via the injected ILogger, AND populate the Error property with a message in the format "Failed to open {filePath}: {ExceptionType}"; LogError-level diagnostics SHALL only be published for access errors — non-access scan issues (corrupted files, unsupported formats) SHALL be logged at LogInformation level only
4. IF the file does not exist at the specified path, THEN THE FileIndex SHALL skip scanning, log at LogError level a diagnostic message containing the file path via the injected ILogger, AND populate the Error property with a message in the format "Failed to open {filePath}: FileNotFoundException"

### Requirement 2: Quick Scan — Line Ending Detection

**User Story:** As a user, I want the application to quickly identify all line boundaries in the file, so that line count and byte-based line lengths are available as soon as possible.

#### Acceptance Criteria

1. WHEN a file is selected in the Open_File_Dialog, THE FileIndex SHALL perform the Quick_Scan as the first scanning phase
2. WHEN performing the Quick_Scan, THE FileIndex SHALL identify line endings by scanning raw bytes for LF (0x0A), CR (0x0D), and CRLF (0x0D 0x0A) delimiters, treating each as a single line boundary
3. WHEN the Quick_Scan completes, THE Line_Index SHALL contain the Byte_Length for every line in the file, where Byte_Length is the number of bytes in the line including the line-ending delimiter bytes (LF=1 byte, CR=1 byte, CRLF=2 bytes); the final line, if not terminated by a delimiter, stores only its content bytes
4. WHEN the Quick_Scan completes, THE Line_Index SHALL contain the total number of lines in the file, where a final segment of bytes not terminated by a line-ending delimiter counts as a line, and an empty file (zero bytes) yields a line count of zero
5. IF a file read error occurs during the Quick_Scan, or if the scan is aborted for any other reason (including user cancellation or memory limits), THEN THE FileIndex SHALL abort the scan, prevent any population of the Line_Index, and report the failure without producing a partial Line_Index

### Requirement 3: Full Scan — Character Length Computation

**User Story:** As a user, I want the application to compute the visible character length of each line, so that I can see accurate character-based metrics for the file.

#### Acceptance Criteria

1. WHEN the Quick_Scan completes successfully, THE FileIndex SHALL perform the Full_Scan as the second scanning phase without requiring additional user action
2. WHEN performing the Full_Scan, THE FileIndex SHALL decode line content using the file's detected encoding and compute Char_Length per line, where Char_Length is the count of .NET characters (UTF-16 code units) in the decoded line content excluding line ending characters and excluding any BOM character
3. WHEN the Full_Scan completes, THE Line_Index SHALL contain the Char_Length for every line in the file; IF the Line_Index cannot be populated due to a storage error or memory issue, THEN THE Full_Scan SHALL be treated as failed (ScanState transitions to Failed); retry is caller-triggered by disposing the current FileIndex and creating a new instance
4. IF the Full_Scan encounters bytes that are invalid for the detected encoding during a Full_Scan operation, THEN THE FileIndex SHALL decode those bytes using the encoding's replacement character (U+FFFD) and count each replacement as one character toward Char_Length; replacement characters SHALL only be applied during Full_Scan operations

### Requirement 4: Thread-Safe Index Structure

**User Story:** As a developer, I want the Line_Index to be safe for concurrent reads, so that multiple consumers can query the index without synchronization issues.

#### Acceptance Criteria

1. WHEN concurrent read access occurs, THE Line_Index SHALL support at least 4 simultaneous reader threads while a single writer thread is appending data, such that every read returns a complete, previously-written value and never a torn or partially-updated value; this guarantee is conditional on actual concurrent access occurring. IF any intermediate or partially-updated state becomes visible to a reader thread, THE requirement SHALL be considered violated regardless of thread capacity.
2. WHILE the Quick_Scan or Full_Scan is appending entries to the Line_Index, THE Line_Index SHALL guarantee that a line entry is not visible to readers until both its index position and its length value for the current scan phase have been fully written
3. WHILE the Full_Scan is writing Char_Length to an existing line entry, THE Line_Index SHALL ensure readers observe either the entry without Char_Length (as left by Quick_Scan) or the entry with the fully written Char_Length, never an intermediate state
4. THE Line_Index SHALL store each line's Byte_Length and Char_Length as a pair within the same segment, using a single integer tier determined by the larger of the two values (Byte_Length, since it includes delimiter bytes and Char_Length is always less than or equal to Byte_Length); both values in a pair use the same tier width
5. THE Line_Index SHALL permit only a single writer thread to modify the index at any given time; concurrent writes from multiple threads are not supported

### Requirement 5: Memory-Compact Storage

**User Story:** As a user, I want the index to use minimal memory, so that very large files (millions of lines with potentially very long lines) can be indexed without excessive memory consumption.

#### Acceptance Criteria

1. THE Line_Index SHALL use a single SegmentDirectory with a segmented storage strategy where consecutive lines are grouped into variable-length segments; each segment stores pairs of (Byte_Length, Char_Length) per line using one of four unsigned integer tiers: byte (0–255), ushort (0–65,535), uint (0–4,294,967,295), or ulong (>4,294,967,295); the tier for a segment is determined by the maximum Byte_Length in that segment (since Char_Length ≤ Byte_Length, both values fit in the same tier)
2. THE Line_Index SHALL minimize total memory consumption (data bytes + per-segment metadata overhead) when deciding segment boundaries, such that a segment is only split into narrower-tier segments when the memory saved by using a narrower type exceeds the additional per-segment metadata cost; THE system SHALL be prohibited from splitting a segment when the memory saved does not exceed the metadata cost
3. THE Line_Index SHALL support both widening (transitioning to a wider tier) and narrowing (transitioning to a narrower tier) at segment boundaries based on the Byte_Length values of subsequent lines
4. THE Line_Index SHALL maintain the SegmentDirectory that maps line indices to their containing segment for O(log N) or better lookup
5. WHEN the file contains zero lines, THE Line_Index SHALL store no segments and consume no per-line memory; metadata overhead is permitted even when no segments exist, and segment metadata is permitted for any non-empty file including single-line files

### Requirement 6: Resource Disposal

**User Story:** As a developer, I want FileIndex to cleanly release resources when disposed, so that the caller can manage FileIndex lifetime without leaks.

#### Acceptance Criteria

1. THE FileIndex SHALL accept a CancellationToken and an ILogger<FileIndex> (Microsoft.Extensions.Logging) at construction; when the token is signalled, THE FileIndex SHALL stop scanning within 500 milliseconds, where "stop scanning" means: no new file I/O operations are issued AND ScanState has transitioned to Cancelled within 500ms; resource cleanup (buffer deallocation, handle closure) may continue beyond the 500ms boundary
2. THE FileIndex SHALL implement IDisposable and release all resources (file handles, buffers, index memory) when disposed
3. IF the FileIndex fails to release a resource during disposal, THEN THE FileIndex SHALL log the failure via the injected ILogger and continue disposing remaining resources without throwing an exception
4. THE FileIndex SHALL have no dependency on or awareness of callers, consumers, or the UI layer; it SHALL expose thread-safe readable fields and accept a CancellationToken and ILogger<FileIndex> at construction — nothing else
5. WHEN the CancellationToken is signalled, partial cleanup is permitted where resources that can be released within 500ms are released immediately, and remaining resources SHALL be guaranteed to eventually be released after the timeout; cleanup SHALL NOT be abandoned under any circumstances
6. THE FileIndex SHALL use the injected ILogger<FileIndex> for all diagnostic output: scan start and phase transitions at LogInformation level, access errors at LogError level, disposal events at LogDebug level

### Requirement 7: Scan State and Error Exposure

**User Story:** As a developer, I want FileIndex to expose its scan progress and any error via thread-safe fields, so that any consumer can read current state without coordination.

#### Acceptance Criteria

1. THE FileIndex SHALL expose a ScanState property (public getter, no public setter) indicating the current phase: NotStarted, QuickScanInProgress, QuickScanComplete, FullScanInProgress, FullScanComplete, Failed, or Cancelled
2. WHEN the Quick_Scan completes successfully, THE FileIndex SHALL transition ScanState to QuickScanComplete
3. WHEN the Full_Scan completes successfully, THE FileIndex SHALL transition ScanState to FullScanComplete
4. IF a scan fails, THEN THE FileIndex SHALL transition ScanState to Failed and expose an Error property containing the failure reason in the format "Failed to open {filePath}: {ExceptionType}" or "Scan failed for {filePath}: {ExceptionType}"
5. IF the CancellationToken is signalled, THEN THE FileIndex SHALL transition ScanState to Cancelled
6. THE ScanState and Error properties SHALL be safe to read from any thread at any time without synchronization by the caller
7. THE Line_Index data SHALL be readable by any thread once ScanState reaches QuickScanComplete or later

### Requirement 8: Encoding Exposure

**User Story:** As a consumer (e.g. File_View_Service), I need to obtain the detected file encoding from FileIndex, so that line bytes can be correctly decoded into characters.

#### Acceptance Criteria

1. THE FileIndex SHALL expose a public property `Encoding` of type `System.Text.Encoding` returning the encoding detected during scan (UTF-8, UTF-16 LE, UTF-16 BE, UTF-32 LE, UTF-32 BE; defaults to UTF-8 when no BOM present)
2. THE FileIndex SHALL expose a public property `BomByteLength` of type `int` returning the number of BOM bytes at file start (0 if no BOM, 3 for UTF-8, 2 for UTF-16, 4 for UTF-32)
3. THE `Encoding` and `BomByteLength` properties SHALL be available immediately after scan starts (encoding detection is first operation before line indexing); they SHALL never be null once scan has started

### Requirement 9: Caller Responsibilities (UI Integration)

**User Story:** As a user, I want to see scan results as they become available and error messages when scans fail, so that I get immediate feedback about the file.

#### Acceptance Criteria

1. THE caller SHALL be responsible for creating, polling, and disposing the FileIndex instance
2. THE caller SHALL periodically read the FileIndex ScanState and update the Status_Display accordingly
3. WHEN the caller observes ScanState = QuickScanComplete, THE Status_Display SHALL show the total number of lines and the longest Byte_Length beside the file name
4. WHEN the caller observes ScanState = FullScanComplete, THE Status_Display SHALL show the longest Char_Length in addition to the line count and longest Byte_Length already displayed, retaining all previously shown metrics
5. WHILE the caller observes ScanState = QuickScanInProgress or FullScanInProgress, THE Status_Display SHALL display a visible scanning indicator element beside the file name; the indicator may be hidden if the UI component is unmounted or hidden for other UI-state reasons
6. IF the caller observes ScanState = Failed or Cancelled, THEN THE Status_Display SHALL not display metrics from the failed or cancelled scan and SHALL revert to the state prior to the failed scan; reversion SHALL occur only when the caller actively observes the failed or cancelled state
7. WHEN a new file is selected, THE caller SHALL signal cancellation on the previous FileIndex's CancellationToken, dispose it, then create a new FileIndex instance
8. WHEN a new file is selected for scanning, THE caller SHALL clear all metrics from the previous file before displaying the scanning indicator for the new file
9. IF the caller observes ScanState = Failed, THEN THE caller SHALL display the FileIndex Error field in the main content area, replacing the default "hello world" text

### Requirement 10: Byte Offset Query Correctness and Performance

**User Story:** As a user, I want line-to-byte navigation to stay accurate and responsive even on very large files, so that scrolling and viewport updates remain smooth.

#### Acceptance Criteria

1. WHEN `GetByteOffset(lineIndex)` is called for any valid line index, THEN THE Line_Index SHALL return exactly the cumulative sum of `Byte_Length` values for lines `[0..lineIndex-1]`
2. WHEN `GetByteOffset(0)` and `GetByteOffset(LineCount)` are called, THEN THE Line_Index SHALL return `0` and total file size in bytes respectively
3. WHEN `GetByteOffset(lineIndex)` is called for large line indices near end-of-file, THEN THE Line_Index SHALL compute the result using segment-indexed prefix metadata and SHALL NOT perform full per-line accumulation from line `0`
4. WHEN `GetByteOffset` is called repeatedly for nearby line indices, THEN THE Line_Index SHALL reuse segment-locality information to avoid repeating global prefix recomputation
5. WHEN byte offsets are optimized, THEN existing `GetByteLength`, `GetCharLength`, scan publication ordering, and `Clear()` reset behavior SHALL remain unchanged
