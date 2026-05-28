# Requirements Document

## Introduction

Open File Dialog feature for TextViewer. User presses Ctrl+O → system open-file dialog appears → user picks file → selected file name replaces "Hello World" message in UI. Bridges Angular frontend (keyboard shortcut, display) with .NET backend (native dialog) via Photino web message bridge.

## Glossary

- **Application**: The TextViewer desktop application built with Photino.Blazor hosting an Angular frontend
- **Frontend**: The Angular 19 standalone component rendered inside the Photino webview
- **Backend**: The .NET 10 host process managing the Photino window and native OS interactions
- **Message_Bridge**: The Photino bidirectional communication channel — `window.external.sendMessage` (JS→.NET) and `SendWebMessage` (.NET→JS)
- **Open_File_Dialog**: The native operating system file-selection dialog provided by the OS
- **Display_Area**: The UI region in app.component.html currently showing "Hello World" text

## Requirements

### Requirement 1: Keyboard Shortcut Detection

**User Story:** As a user, I want to press Ctrl+O to open a file, so that I can quickly select a file to view.

#### Acceptance Criteria

1. WHEN the user presses Ctrl+O on Windows/Linux or Cmd+O on macOS, THE Frontend SHALL send an "open-file" message to the Backend via the Message_Bridge
2. WHILE the Frontend is awaiting a response from the Backend for a previous "open-file" message, THE Frontend SHALL not send additional "open-file" messages on subsequent Ctrl+O (or Cmd+O) key presses
3. THE Frontend SHALL prevent the browser default behavior for the Ctrl+O (or Cmd+O) key combination regardless of dialog state
4. WHEN the Frontend receives a file-selection response from the Backend via the Message_Bridge, THE Frontend SHALL transition out of the awaiting-response state and resume accepting Ctrl+O (or Cmd+O) key presses

### Requirement 2: Native File Dialog Invocation

**User Story:** As a user, I want to see the standard OS file dialog, so that I can browse and select a file using familiar system UI.

#### Acceptance Criteria

1. WHEN the Backend receives an "open-file" message from the Message_Bridge, THE Backend SHALL display the native Open_File_Dialog
2. THE Open_File_Dialog SHALL allow the user to select exactly one file and SHALL not restrict the selectable file types
3. WHEN the user selects a file and confirms the dialog, THE Backend SHALL send the full absolute file path of the selected file to the Frontend via the Message_Bridge
4. WHEN the user cancels the Open_File_Dialog, THE Backend SHALL send an empty string to the Frontend via the Message_Bridge
5. IF the Backend receives an "open-file" message while the Open_File_Dialog is already displayed, THEN THE Backend SHALL ignore the message and not open a second dialog

### Requirement 3: File Path Display

**User Story:** As a user, I want to see the full path of the selected file in the UI, so that I have confirmation of which file I chose and where it is located.

#### Acceptance Criteria

1. WHEN the Frontend receives a non-empty string from the Message_Bridge, THE Frontend SHALL replace the Display_Area content with the full string value as received
2. WHEN the Frontend receives an empty string from the Message_Bridge, THE Frontend SHALL retain the current Display_Area content unchanged

### Requirement 4: Initial State

**User Story:** As a user, I want to see a default message when no file has been selected, so that I know the application is ready.

#### Acceptance Criteria

1. WHEN the Application window is first displayed, THE Display_Area SHALL show the text "Hello World"
2. WHILE no file name has been received from the Message_Bridge, THE Display_Area SHALL continue to display "Hello World"
