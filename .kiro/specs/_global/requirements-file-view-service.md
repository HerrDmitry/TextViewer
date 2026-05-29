# File View Service — Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

File View Service feature. Provides a backend service that produces a rectangular text view from a file given a start line, start column, number of rows, and number of columns. Uses the existing FileIndex (line byte offsets and lengths) for fast random access, reading only bytes needed for the requested view region. Result is a list of strings representing visible rows of the requested viewport.

## Glossary

- **File_View_Service**: C# backend service producing rectangular text views from an indexed file
- **FileIndex**: Existing C# class scanning a file and building a thread-safe index of per-line byte/char lengths (see `requirements-file-index.md`)
- **Line_Index**: Internal data structure within FileIndex storing per-line metadata (byte length, char length)
- **View_Request**: Request specifying start line, start column, row count, column count for desired viewport
- **View_Result**: Output of File_View_Service: list of strings representing rows of requested viewport
- **Start_Line**: 0-based line number of first row in view (top edge)
- **Start_Column**: 0-based column position of first character in each row (left edge)
- **Row_Count**: Number of rows requested
- **Column_Count**: Number of columns (characters) requested per row

## Requirements

### Requirement 1: View Extraction

**User Story:** As a caller, I want to request a rectangular region of a file by specifying start line, start column, row count, and column count, so that I can display a viewport of the file content.

#### Acceptance Criteria

1. WHEN a View_Request is received with Start_Line ≥ 0, Start_Column ≥ 0, Row_Count ≥ 1, and Column_Count ≥ 1, THE File_View_Service SHALL return a View_Result containing a list of row strings (see below for count rules)
2. WHEN extracting a row, THE File_View_Service SHALL decode the file line starting at the byte offset from FileIndex, skip Start_Column characters, and return up to Column_Count content characters followed by the line's original delimiter; IF fewer than Column_Count characters remain after Start_Column, THE row string SHALL contain only available characters followed by delimiter (no space padding)
3. IF a requested line exists but its content length (excluding delimiter) ≤ Start_Column, THEN THE File_View_Service SHALL return only the line's delimiter for that row (empty content + delimiter); IF the line has no delimiter (last line), return empty string
4. **Scan in progress**: IF scan still running AND request extends beyond currently scanned range, THE File_View_Service SHALL return empty strings for rows beyond scanned range (partial result up to Row_Count total)
5. **Scan complete**: IF scan complete AND request extends beyond total file lines, THE File_View_Service SHALL return only rows that exist (result may contain fewer than Row_Count strings)
6. **Completely outside file**: IF Start_Line ≥ total file line count (scan complete), THE File_View_Service SHALL return a single empty string
7. **Empty file**: IF file is empty (0 lines after scan complete), THE File_View_Service SHALL return a single empty string regardless of request parameters
8. IF a View_Request is received with Start_Line < 0, Start_Column < 0, Row_Count < 1, or Column_Count < 1, THEN THE File_View_Service SHALL reject the request and return an error indicating which parameter is out of range

### Requirement 2: FileIndex Lifecycle

**User Story:** As a caller, I want to create a File_View_Service with a file path and have it manage its own FileIndex, so that I don't need to manage indexing separately.

#### Acceptance Criteria

1. THE File_View_Service SHALL accept a file path and a CancellationToken as constructor parameters; it SHALL create a private FileIndex instance passing the same CancellationToken
2. THE File_View_Service SHALL initiate the FileIndex scan (StartScanAsync) upon construction or via an explicit initialization method
3. THE File_View_Service SHALL accept View_Requests at any time after scan has started (including during QuickScan); lines within already-scanned range served from available data; lines beyond scanned range return empty strings
4. THE File_View_Service SHALL expose a `ScanState` property reusing the existing `ScanState` enum from FileIndex (NotStarted, QuickScanInProgress, QuickScanComplete, FullScanInProgress, FullScanComplete, Failed, Cancelled), so callers can distinguish "not yet scanned" empty rows from "past EOF" short results
5. THE File_View_Service SHALL dispose of its FileIndex when itself disposed
6. WHEN CancellationToken is cancelled, THE File_View_Service SHALL stop any in-progress view extraction and the underlying FileIndex scan SHALL also cancel gracefully

### Requirement 3: Input Validation

**User Story:** As a caller, I want clear error feedback when I provide invalid view parameters, so that I can correct my request.

#### Acceptance Criteria

1. IF Start_Line < 0, THEN return error indicating Start_Line must be ≥ 0
2. IF Start_Column < 0, THEN return error indicating Start_Column must be ≥ 0
3. IF Row_Count < 1, THEN return error indicating Row_Count must be ≥ 1
4. IF Column_Count < 1, THEN return error indicating Column_Count must be ≥ 1
5. THE File_View_Service SHALL validate all four parameters before any file I/O or FileIndex lookup; IF multiple invalid, return error for at least the first invalid parameter detected

### Requirement 4: Character Decoding

**User Story:** As a user, I want the view to correctly display characters regardless of file encoding, so that multi-byte encoded files render properly.

#### Acceptance Criteria

1. THE File_View_Service SHALL obtain file encoding from FileIndex (detected via BOM: UTF-8, UTF-16 LE, UTF-16 BE, UTF-32 LE, UTF-32 BE; defaults to UTF-8 when no BOM) and use that encoding to decode line bytes into characters
2. WHEN decoding bytes invalid for detected encoding, THE File_View_Service SHALL substitute U+FFFD and count each replacement as one character toward column positioning
3. THE File_View_Service SHALL treat each decoded .NET character (UTF-16 code unit) as exactly one column position, including each code unit of a surrogate pair (supplementary char = 2 columns) and control characters such as tab (U+0009)
4. Line-ending delimiters (\n, \r\n, \r) appended to each row string as they appear in file but NOT counted toward column positioning or Column_Count limit; last line MAY have no delimiter
5. IF file begins with BOM, THE File_View_Service SHALL exclude BOM from column counting and view output

### Requirement 5: Concurrent Access Safety

**User Story:** As a developer, I want the File_View_Service to be safe for concurrent view requests, so that multiple consumers can request views simultaneously.

#### Acceptance Criteria

1. THE File_View_Service SHALL support ≥ 4 concurrent View_Requests against same FileIndex without external synchronization, each producing correct and complete View_Result independent of other in-flight requests
2. THE File_View_Service SHALL open file with FileShare.ReadWrite and strictly FileAccess.Read for view extraction, using independent file handle per request so concurrent seek/read operations don't interfere
3. THE File_View_Service SHALL not modify FileIndex or Line_Index state during view extraction; all interactions read-only (GetByteLength, GetByteOffset, LineCount)

### Requirement 6: Error Handling

**User Story:** As a caller, I want clear error information when view extraction fails, so that I can handle failures gracefully.

#### Acceptance Criteria

1. THE File_View_Service SHALL use Result pattern (`Result<ViewResult, ViewError>`) for all view operations; errors NOT thrown as exceptions except OperationCanceledException on cancellation
2. IF file read error (IOException or UnauthorizedAccessException) occurs during extraction, THEN return ViewError with file path and error type
3. IF file deleted/moved since FileIndex built, THEN return ViewError indicating file no longer accessible
4. WHEN CancellationToken cancelled, throw OperationCanceledException for in-progress or subsequent requests
5. ViewError SHALL include error code enum (InvalidParameter, FileNotAccessible, IoError, Cancelled) and human-readable message
