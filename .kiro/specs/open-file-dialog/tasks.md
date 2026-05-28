# Implementation Plan: Open File Dialog

## Overview

Implements Ctrl+O → native file dialog → display full file path flow. Angular frontend captures keyboard shortcut and communicates with .NET backend via Photino message bridge. Backend shows OS file dialog and returns selected path. Frontend displays the received path string directly in the UI.

## Tasks

- [x] 1. Implement frontend keyboard handling and state management
  - [x] 1.1 Add signals and keyboard listener to AppComponent
    - Add `displayText` signal initialized to `'Hello World'`
    - Add `awaitingResponse` signal initialized to `false`
    - Add `@HostListener('document:keydown', ['$event'])` handler that detects Ctrl+O (Windows/Linux) and Cmd+O (macOS)
    - Call `preventDefault()` on the event regardless of dialog state
    - If not awaiting, call `window.external.sendMessage('open-file')` and set `awaitingResponse` to `true`
    - _Requirements: 1.1, 1.2, 1.3_

  - [x] 1.2 Add message receiver to AppComponent
    - Register a Photino message listener (via `window.external.receiveMessage` or equivalent) in component constructor/init
    - On receiving a non-empty string, set `displayText` to the received string directly
    - On receiving an empty string, leave `displayText` unchanged
    - Always set `awaitingResponse` to `false` on any response
    - _Requirements: 1.4, 3.1, 3.2_

  - [x] 1.3 Update app.component.html template
    - Replace static `<h1>Hello World</h1>` with `<h1>{{ displayText() }}</h1>` to bind to the signal
    - _Requirements: 4.1, 4.2_

  - [x] 1.4 Write property test for state guard (Property 1)
    - **Property 1: State guard prevents duplicate sends**
    - Generate random sequences of `{keypress, response}` events using fast-check
    - Assert that at most 1 outstanding `sendMessage` call exists at any time (no duplicate sends without intervening response)
    - Minimum 100 iterations
    - **Validates: Requirements 1.2, 1.4**

- [x] 2. Checkpoint - Frontend tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [ ] 3. Implement backend message handler
  - [x] 3.1 Register WebMessageReceived handler in Program.cs
    - Add `app.MainWindow.RegisterWebMessageReceivedHandler` after window configuration
    - Check if received message equals `"open-file"`
    - Ignore any other messages silently
    - _Requirements: 2.1, 2.5_

  - [x] 3.2 Implement native file dialog invocation
    - When `"open-file"` is received, show native `OpenFileDialog` (single file, no type filter)
    - On file selection, call `SendWebMessage` with the full absolute path
    - On cancel, call `SendWebMessage` with empty string `""`
    - Wrap dialog call in try/catch — on exception, send empty string
    - _Requirements: 2.2, 2.3, 2.4_

- [x] 4. Checkpoint - Backend builds and integration works
  - Ensure all tests pass, ask the user if questions arise.

- [x] 5. Integration wiring and final verification
  - [x] 5.1 Verify end-to-end message flow
    - Ensure Angular `sendMessage` call reaches .NET handler
    - Ensure .NET `SendWebMessage` response reaches Angular listener
    - Verify Ctrl+O → dialog → path displayed in UI works end-to-end
    - _Requirements: 1.1, 2.1, 2.3, 3.1_

  - [x] 5.2 Write unit tests for frontend keyboard and display logic
    - Test Ctrl+O triggers `sendMessage("open-file")`
    - Test other key combos don't trigger send
    - Test `preventDefault` called on Ctrl+O
    - Test Cmd+O works (meta key)
    - Test initial `displayText` is "Hello World"
    - Test non-empty response sets `displayText` to full received string
    - Test empty response leaves display unchanged
    - _Requirements: 1.1, 1.3, 3.1, 3.2, 4.1, 4.2_

- [x] 6. Final checkpoint - All tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property 1 test validates the state guard using fast-check (PBT library)
- Frontend displays the received path string directly — no base name extraction needed
- TypeScript for frontend tasks, C# for backend tasks

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["1.2", "1.3"] },
    { "id": 2, "tasks": ["1.4", "3.1"] },
    { "id": 3, "tasks": ["3.2"] },
    { "id": 4, "tasks": ["5.1"] },
    { "id": 5, "tasks": ["5.2"] }
  ]
}
```
