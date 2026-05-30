# Global Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]
#[[file:.kiro/specs/_global/requirements-file-index.md]]
#[[file:.kiro/specs/_global/requirements-file-view-service.md]]
#[[file:.kiro/specs/_global/requirements-viewer-ui-shell.md]]

## Introduction

TextViewer is a cross-platform desktop application for viewing text content. This document captures all shipped product requirements. Infrastructure/platform context provided by requirements-shared.md. Feature-specific detailed requirements in separate docs (referenced above).

## Glossary

- **Open_File_Dialog**: The native operating system file-selection dialog provided by the OS
- **FileIndex**: C# class orchestrating two-phase file scanning (see `requirements-file-index.md` for full glossary)
- **Line_Index**: Per-line length metadata store within FileIndex (see `requirements-file-index.md`)
- **Status_Display**: UI region beside file name showing scan metrics (see `requirements-file-index.md`)
- **File_View_Service**: C# backend service producing rectangular text views (see `requirements-file-view-service.md` for full glossary)
- **View_Request**: Request specifying viewport parameters (see `requirements-file-view-service.md`)
- **View_Result**: List of row strings representing the viewport (see `requirements-file-view-service.md`)
- **UI_Shell**: Top-level Angular layout — Menu_Bar, Tab_Container, Text_View_Area, Status_Bar (see `requirements-viewer-ui-shell.md` for full glossary)

## Requirements

### Requirement 1: Application Window Configuration

**User Story:** As a user, I want the application window to have a reasonable default size and title, so that it looks like a proper desktop application.

#### Acceptance Criteria

1. THE Photino_Window SHALL display a title of "Text Viewer" in the window title bar
2. THE Photino_Window SHALL open with a default size that is appropriate for the user's display
3. THE Photino_Window SHALL be resizable by the user

### Requirement 2: Native File Dialog Invocation

**User Story:** As a user, I want to see the standard OS file dialog, so that I can browse and select a file using familiar system UI.

#### Acceptance Criteria

1. WHEN the Backend's Message_Bus_Host receives an "open-file" message via its registered handler, THE Backend SHALL display the native Open_File_Dialog
2. THE Open_File_Dialog SHALL allow the user to select exactly one file and SHALL not restrict the selectable file types
3. WHEN the user selects a file and confirms the dialog, THE Backend handler SHALL return the full absolute file path as the response payload
4. WHEN the user cancels the Open_File_Dialog, THE Backend handler SHALL return an empty string as the response payload
5. IF the Backend receives an "open-file" message while the Open_File_Dialog is already displayed, THEN THE Backend SHALL ignore the message and not open a second dialog

### Requirement 3: Viewer UI Shell

**User Story:** As a user, I want a tabbed document interface with menu, content area, and status bar, so that I can open, view, and manage multiple files.

#### Acceptance Criteria

Full spec in `requirements-viewer-ui-shell.md`. Summary:

1. THE UI_Shell SHALL provide Menu_Bar (File → Open..., Exit), Tab_Container, Text_View_Area, and Status_Bar
2. THE UI_Shell SHALL manage tabs (create on file open, close, activate, adjacency selection)
3. THE UI_Shell SHALL integrate with Message_Bus_Client for open-file and exit actions
4. THE UI_Shell SHALL persist tab position preference (top/bottom) to localStorage
5. THE UI_Shell SHALL display Empty_State_Prompt ("Ctrl-O to open a file") when no tabs open
6. THE UI_Shell SHALL display active file path in Status_Bar

### Requirement 4: File Index — Two-Phase Scanning

**User Story:** As a user, I want opened files to be scanned for line metadata, so that line count and length metrics are available progressively.

#### Acceptance Criteria

1. WHEN a file is selected, THE application SHALL perform Quick_Scan (byte lengths) then Full_Scan (char lengths) automatically — full spec in `requirements-file-index.md`
2. THE FileIndex SHALL open files non-exclusively (FileShare.ReadWrite, FileAccess.Read)
3. THE Line_Index SHALL be thread-safe (single writer, multiple concurrent readers, no torn reads)
4. THE Line_Index SHALL use memory-compact segmented storage with tiered integer widths
5. THE FileIndex SHALL expose ScanState and Error as thread-safe polling fields
6. THE caller SHALL manage FileIndex lifecycle (create, poll, dispose) and update Status_Display

### Requirement 5: File Index — Status Display

**User Story:** As a user, I want to see scan progress and results beside the file name, so that I get immediate feedback.

#### Acceptance Criteria

1. WHILE scanning, THE Status_Display SHALL show a scanning indicator
2. WHEN QuickScanComplete, THE Status_Display SHALL show line count + max Byte_Length
3. WHEN FullScanComplete, THE Status_Display SHALL additionally show max Char_Length
4. IF scan fails or is cancelled, THE Status_Display SHALL revert to pre-scan state
5. IF scan fails, THE main content area SHALL display the error message

### Requirement 6: File View Service — Rectangular View Extraction

**User Story:** As a caller, I want to request a rectangular region of a file by specifying viewport parameters, so that I can display file content efficiently.

#### Acceptance Criteria

1. THE File_View_Service SHALL produce rectangular text views given (startLine, startCol, rowCount, colCount) — full spec in `requirements-file-view-service.md`
2. THE File_View_Service SHALL own a private FileIndex, manage its lifecycle, and expose ScanState for observation
3. THE File_View_Service SHALL decode bytes using FileIndex-detected encoding (UTF-8/16/32, BOM-aware)
4. THE File_View_Service SHALL support ≥ 4 concurrent requests via independent file handles (FileAccess.Read, FileShare.ReadWrite)
5. THE File_View_Service SHALL use Result pattern for errors; OperationCanceledException for cancellation
6. Column = .NET char (UTF-16 code unit); delimiters appended but not counted toward Column_Count
