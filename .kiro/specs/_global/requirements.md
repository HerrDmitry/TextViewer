# Global Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]
#[[file:.kiro/specs/_global/requirements-file-index.md]]

## Introduction

TextViewer is a cross-platform desktop application for viewing text content. This document captures all shipped product requirements. Infrastructure/platform context provided by requirements-shared.md. Feature-specific detailed requirements in separate docs (referenced above).

## Glossary

- **Open_File_Dialog**: The native operating system file-selection dialog provided by the OS
- **Display_Area**: The UI region in app.component.html showing current text content
- **Hello_World_View**: The initial view displayed to the user upon application launch
- **FileIndex**: C# class orchestrating two-phase file scanning (see `requirements-file-index.md`)
- **Line_Index**: Per-line length metadata store within FileIndex
- **Status_Display**: UI region beside file name showing scan metrics

## Requirements

### Requirement 1: Application Window Configuration

**User Story:** As a user, I want the application window to have a reasonable default size and title, so that it looks like a proper desktop application.

#### Acceptance Criteria

1. THE Photino_Window SHALL display a title of "Text Viewer" in the window title bar
2. THE Photino_Window SHALL open with a default size that is appropriate for the user's display
3. THE Photino_Window SHALL be resizable by the user

### Requirement 2: Hello World Display

**User Story:** As a user, I want to see a "Hello World" message when the application starts, so that I can confirm the application is working correctly.

#### Acceptance Criteria

1. WHEN the Photino_Window finishes loading, THE Angular_Frontend SHALL display the text "Hello World" in the Hello_World_View
2. THE Hello_World_View SHALL be the default view rendered on application startup

### Requirement 3: Keyboard Shortcut — Open File

**User Story:** As a user, I want to press Ctrl+O to open a file, so that I can quickly select a file to view.

#### Acceptance Criteria

1. WHEN the user presses Ctrl+O on Windows/Linux or Cmd+O on macOS, THE Frontend SHALL send an "open-file" message to the Backend via Message_Bus_Client.send — direct calls to window.external.sendMessage SHALL be prohibited
2. WHILE the Frontend is awaiting a response from the Backend for a previous "open-file" message (pendingCorrelationId is non-null), THE Frontend SHALL not send additional "open-file" messages on subsequent Ctrl+O (or Cmd+O) key presses
3. THE Frontend SHALL prevent the browser default behavior for the Ctrl+O (or Cmd+O) key combination regardless of dialog state
4. WHEN the Frontend receives a file-selection response via Message_Bus_Client subscription, THE Frontend SHALL clear the pending state and resume accepting Ctrl+O (or Cmd+O) key presses

### Requirement 4: Native File Dialog Invocation

**User Story:** As a user, I want to see the standard OS file dialog, so that I can browse and select a file using familiar system UI.

#### Acceptance Criteria

1. WHEN the Backend's Message_Bus_Host receives an "open-file" message via its registered handler, THE Backend SHALL display the native Open_File_Dialog
2. THE Open_File_Dialog SHALL allow the user to select exactly one file and SHALL not restrict the selectable file types
3. WHEN the user selects a file and confirms the dialog, THE Backend handler SHALL return the full absolute file path as the response payload
4. WHEN the user cancels the Open_File_Dialog, THE Backend handler SHALL return an empty string as the response payload
5. IF the Backend receives an "open-file" message while the Open_File_Dialog is already displayed, THEN THE Backend SHALL ignore the message and not open a second dialog

### Requirement 5: File Path Display

**User Story:** As a user, I want to see the full path of the selected file in the UI, so that I have confirmation of which file I chose and where it is located.

#### Acceptance Criteria

1. WHEN the Frontend receives a non-empty payload via its Message_Bus_Client "open-file" subscription, THE Frontend SHALL replace the Display_Area content with the full string value as received
2. WHEN the Frontend receives an empty string payload via its Message_Bus_Client "open-file" subscription, THE Frontend SHALL retain the current Display_Area content unchanged

### Requirement 6: Initial Display State

**User Story:** As a user, I want to see a default message when no file has been selected, so that I know the application is ready.

#### Acceptance Criteria

1. WHEN the Application window is first displayed, THE Display_Area SHALL show the text "Hello World"
2. WHILE no file name has been received from the Message_Bridge, THE Display_Area SHALL continue to display "Hello World"


### Requirement 7: File Index — Two-Phase Scanning

**User Story:** As a user, I want opened files to be scanned for line metadata, so that line count and length metrics are available progressively.

#### Acceptance Criteria

1. WHEN a file is selected, THE application SHALL perform Quick_Scan (byte lengths) then Full_Scan (char lengths) automatically — full spec in `requirements-file-index.md`
2. THE FileIndex SHALL open files non-exclusively (FileShare.ReadWrite, FileAccess.Read)
3. THE Line_Index SHALL be thread-safe (single writer, multiple concurrent readers, no torn reads)
4. THE Line_Index SHALL use memory-compact segmented storage with tiered integer widths
5. THE FileIndex SHALL expose ScanState and Error as thread-safe polling fields
6. THE caller SHALL manage FileIndex lifecycle (create, poll, dispose) and update Status_Display

### Requirement 8: File Index — Status Display

**User Story:** As a user, I want to see scan progress and results beside the file name, so that I get immediate feedback.

#### Acceptance Criteria

1. WHILE scanning, THE Status_Display SHALL show a scanning indicator
2. WHEN QuickScanComplete, THE Status_Display SHALL show line count + max Byte_Length
3. WHEN FullScanComplete, THE Status_Display SHALL additionally show max Char_Length
4. IF scan fails or is cancelled, THE Status_Display SHALL revert to pre-scan state
5. IF scan fails, THE main content area SHALL display the error message
