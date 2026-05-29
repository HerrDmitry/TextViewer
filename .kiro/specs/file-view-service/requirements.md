# Requirements Document

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

File View Service feature. Provides a backend service that produces a rectangular text view from a file given a start line, start column, number of rows, and number of columns. The service uses the existing FileIndex (which contains line byte offsets and lengths) for fast random access into the file, reading only the bytes needed for the requested view region. The result is a list of strings representing the visible rows of the requested viewport.

## Glossary

- **File_View_Service**: The C# backend service responsible for producing rectangular text views from an indexed file
- **FileIndex**: The existing C# class that scans a file and builds a thread-safe index of per-line byte lengths and character lengths (see `Services/FileIndex.cs`)
- **Line_Index**: The internal data structure within FileIndex storing per-line metadata (byte length, char length)
- **View_Request**: A request specifying start line, start column, number of rows, and number of columns for the desired viewport
- **View_Result**: The output of the File_View_Service: a list of strings representing the rows of the requested viewport
- **Start_Line**: The 0-based line number of the first row in the view (top edge of viewport)
- **Start_Column**: The 0-based column position of the first character in each row (left edge of viewport)
- **Row_Count**: The number of rows requested in the view
- **Column_Count**: The number of columns (characters) requested per row

## Requirements

### Requirement 1: View Extraction

**User Story:** As a caller, I want to request a rectangular region of a file by specifying start line, start column, row count, and column count, so that I can display a viewport of the file content.

#### Acceptance Criteria

1. WHEN a View_Request is received with Start_Line greater than or equal to 0, Start_Column greater than or equal to 0, Row_Count greater than or equal to 1, and Column_Count greater than or equal to 1, THE File_View_Service SHALL return a View_Result containing a list of row strings (see below for count rules)
2. WHEN extracting a row, THE File_View_Service SHALL decode the file line starting at the byte offset obtained from the FileIndex, skip Start_Column characters, and return up to Column_Count content characters followed by the line's original delimiter; IF fewer than Column_Count characters remain after Start_Column, THE row string SHALL contain only the available characters followed by the delimiter (no space padding)
3. IF a requested line exists but its content length (excluding delimiter) is less than or equal to Start_Column, THEN THE File_View_Service SHALL return only the line's delimiter for that row (empty content + delimiter); IF the line has no delimiter (last line), return an empty string
4. **Scan in progress**: IF the scan is still running AND the request extends beyond the currently scanned range, THE File_View_Service SHALL return empty strings for rows beyond the scanned range (partial result up to Row_Count total)
5. **Scan complete**: IF the scan is complete AND the request extends beyond total file lines, THE File_View_Service SHALL return only the rows that exist in the file (result may contain fewer than Row_Count strings)
6. **Completely outside file**: IF Start_Line is greater than or equal to total file line count (scan complete), THE File_View_Service SHALL return a single empty string
7. **Empty file**: IF the file is empty (0 lines after scan complete), THE File_View_Service SHALL return a single empty string regardless of request parameters
8. IF a View_Request is received with Start_Line less than 0, Start_Column less than 0, Row_Count less than 1, or Column_Count less than 1, THEN THE File_View_Service SHALL reject the request and return an error indication specifying which parameter is out of range

### Requirement 2: FileIndex Lifecycle

**User Story:** As a caller, I want to create a File_View_Service with a file path and have it manage its own FileIndex, so that I don't need to manage indexing separately.

#### Acceptance Criteria

1. THE File_View_Service SHALL accept a file path and a CancellationToken as constructor parameters; it SHALL create a private FileIndex instance passing the same CancellationToken
2. THE File_View_Service SHALL initiate the FileIndex scan (StartScanAsync) upon construction or via an explicit initialization method
3. THE File_View_Service SHALL accept View_Requests at any time after scan has started (including during QuickScan); lines within the already-scanned range are served from available data; lines beyond scanned range return empty strings
4. THE File_View_Service SHALL expose a `ScanState` property that reuses the existing `ScanState` enum from FileIndex (NotStarted, QuickScanInProgress, QuickScanComplete, FullScanInProgress, FullScanComplete, Failed, Cancelled), so callers can distinguish "not yet scanned" empty rows from "past EOF" short results at any point in the lifecycle
5. THE File_View_Service SHALL dispose of its FileIndex when the File_View_Service itself is disposed
6. WHEN the CancellationToken is cancelled, THE File_View_Service SHALL stop any in-progress view extraction and the underlying FileIndex scan SHALL also cancel gracefully

### Requirement 3: FileIndex Encoding Property

**User Story:** As the File_View_Service, I need to obtain the detected file encoding from the FileIndex, so that I can correctly decode line bytes into characters.

#### Acceptance Criteria

1. THE FileIndex SHALL expose a public property `Encoding` of type `System.Text.Encoding` that returns the encoding detected during the scan (UTF-8, UTF-16 LE, UTF-16 BE, UTF-32 LE, UTF-32 BE; defaults to UTF-8 when no BOM present)
2. THE FileIndex SHALL expose a public property `BomByteLength` of type `int` that returns the number of BOM bytes at the start of the file (0 if no BOM, 3 for UTF-8, 2 for UTF-16, 4 for UTF-32)
3. THE FileIndex `Encoding` and `BomByteLength` properties SHALL be available immediately after scan starts (encoding detection is the first operation before any line indexing begins); they SHALL never be null once scan has started

### Requirement 4: Input Validation

**User Story:** As a caller, I want clear error feedback when I provide invalid view parameters, so that I can correct my request.

#### Acceptance Criteria

1. IF Start_Line is less than 0, THEN THE File_View_Service SHALL return an error indicating Start_Line must be at least 0
2. IF Start_Column is less than 0, THEN THE File_View_Service SHALL return an error indicating Start_Column must be at least 0
3. IF Row_Count is less than 1, THEN THE File_View_Service SHALL return an error indicating Row_Count must be at least 1
4. IF Column_Count is less than 1, THEN THE File_View_Service SHALL return an error indicating Column_Count must be at least 1
5. WHEN a View_Request is received, THE File_View_Service SHALL validate all four parameters (Start_Line, Start_Column, Row_Count, Column_Count) before performing any file I/O or FileIndex lookup; IF multiple parameters are invalid, THEN THE File_View_Service SHALL return an error indicating at least the first invalid parameter detected

### Requirement 5: Character Decoding

**User Story:** As a user, I want the view to correctly display characters regardless of file encoding, so that multi-byte encoded files render properly.

#### Acceptance Criteria

1. THE File_View_Service SHALL obtain the file encoding from the FileIndex (detected via BOM: UTF-8, UTF-16 LE, UTF-16 BE, UTF-32 LE, UTF-32 BE; defaults to UTF-8 when no BOM present) and use that encoding to decode line bytes into characters
2. WHEN decoding bytes that are invalid for the detected encoding, THE File_View_Service SHALL substitute the Unicode replacement character (U+FFFD) and count each replacement as one character toward column positioning
3. THE File_View_Service SHALL treat each decoded .NET character (UTF-16 code unit) as occupying exactly one column position, including each code unit of a surrogate pair (a supplementary character occupies two column positions) and including control characters such as tab (U+0009)
4. Line-ending delimiters (\n, \r\n, or \r) are appended to each row string as they appear in the file but are NOT counted toward column positioning or the Column_Count limit; the last line MAY have no delimiter
5. IF the file begins with a BOM (UTF-8: 0xEF 0xBB 0xBF; UTF-16 LE: 0xFF 0xFE; UTF-16 BE: 0xFE 0xFF; UTF-32 LE: 0xFF 0xFE 0x00 0x00; UTF-32 BE: 0x00 0x00 0xFE 0xFF), THE File_View_Service SHALL exclude the BOM from column counting and view output; the BOM is not a visible character in the view

### Requirement 6: Concurrent Access Safety

**User Story:** As a developer, I want the File_View_Service to be safe for concurrent view requests, so that multiple consumers can request views simultaneously.

#### Acceptance Criteria

1. THE File_View_Service SHALL support at least 4 concurrent View_Requests against the same FileIndex without requiring external synchronization by the caller, such that each request produces a correct and complete View_Result independent of other in-flight requests
2. THE File_View_Service SHALL open the file with FileShare.ReadWrite access mode and strictly FileAccess.Read (never FileAccess.Write or FileAccess.ReadWrite) for view extraction, using an independent file handle per request so that concurrent seek and read operations do not interfere with each other and accidental write access is prevented
3. THE File_View_Service SHALL not modify the FileIndex or Line_Index state during view extraction; all interactions with the Line_Index SHALL be read-only queries (GetByteLength, GetByteOffset, LineCount)

### Requirement 7: Error Handling

**User Story:** As a caller, I want clear error information when view extraction fails, so that I can handle failures gracefully.

#### Acceptance Criteria

1. THE File_View_Service SHALL use a Result pattern (e.g., `Result<ViewResult, ViewError>`) for all view operations; errors SHALL NOT be thrown as exceptions except for OperationCanceledException on cancellation
2. IF a file read error (IOException or UnauthorizedAccessException) occurs during view extraction, THEN THE File_View_Service SHALL return a ViewError containing the file path and error type
3. IF the file has been deleted or moved since the FileIndex was built, THEN THE File_View_Service SHALL return a ViewError indicating the file is no longer accessible
4. WHEN the CancellationToken is cancelled, THE File_View_Service SHALL throw OperationCanceledException for any in-progress or subsequent view requests
5. ViewError SHALL include an error code enum (InvalidParameter, FileNotAccessible, IoError, Cancelled) and a human-readable message

