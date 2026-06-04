# Implementation Plan: Scan Progress Bar

## Overview

Adds a progress bar to the status bar showing file scan completion. Backend tracks bytes read during scan and appends progress percentage as a 5th field in `get-scroll-info` responses. Frontend parses the field, stores it in `TabViewState.scanProgress`, and renders a fill bar when scanning is active.

## Tasks

- [x] 1. Backend: Add progress tracking to FileIndex and expose in get-scroll-info
  - [x] 1.1 Add `_bytesRead` and `TotalFileSize` fields to `FileIndex.cs`
    - Add `private volatile long _bytesRead` field
    - Add `public long TotalFileSize { get; private set; }` property (set from `_stream.Length` before scan loop)
    - Add `public long BytesRead => Volatile.Read(ref _bytesRead)` property
    - Increment `_bytesRead` by `bytesRead` return value from each `ReadAsync` call in `RunUnifiedScanAsync`
    - _Requirements: 4.1, 4.6_

  - [x] 1.2 Add pass-through properties to `FileViewService.cs`
    - Add `public long BytesRead => _fileIndex.BytesRead` property
    - Add `public long TotalFileSize => _fileIndex.TotalFileSize` property
    - _Requirements: 4.1_

  - [x] 1.3 Append progress percentage to `HandleGetScrollInfo` in `Program.cs`
    - Compute `progressPercentage = (int)(service.BytesRead * 100 / service.TotalFileSize)` when `TotalFileSize > 0` and scan is in progress
    - Report `100` when scan state is terminal (`>= ScanComplete`) or `TotalFileSize == 0`
    - Append as 5th newline-delimited field: `$"{scanState}\n{lineCount}\n{maxByteLength}\n{maxCharLength}\n{progressPercentage}"`
    - _Requirements: 4.2, 4.3, 4.4, 4.5_

  - [x] 1.4 Write property test for progress percentage computation (C#/FsCheck)
    - **Property 4: Progress percentage computation**
    - **Validates: Requirements 4.3, 4.4, 4.5**

  - [x] 1.5 Write property test for bytes-read invariant after scan (C#/FsCheck)
    - **Property 5: Bytes-read invariant after scan**
    - **Validates: Requirements 4.1**

- [x] 2. Checkpoint - Backend progress tracking
  - Ensure all tests pass, ask the user if questions arise.

- [x] 3. Frontend: Add `scanProgress` to state and parse from poll response
  - [x] 3.1 Add `scanProgress` field to `TabViewState` in `shell.types.ts`
    - Add `scanProgress: number` field (default `0`)
    - Update all places that create initial `TabViewState` objects to include `scanProgress: 0`
    - _Requirements: 3.3_

  - [x] 3.2 Parse 5th field in `handleScrollInfoResponse` in `shell-state.service.ts`
    - When response has 5 fields and first field is `ScanInProgress`, parse `fields[4]` as integer
    - Store parsed value in `tabViewState.scanProgress`
    - If 5th field missing or not parseable, leave `scanProgress` unchanged
    - _Requirements: 3.2_

  - [x] 3.3 Add computed signals `activeScanProgress` and `isScanning` to `ShellStateService`
    - `isScanning`: true iff `activeTabId !== null` AND active tab's effective scan state is `ScanInProgress`
    - `activeScanProgress`: returns active tab's `scanProgress` value (0 if no active tab)
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 5.1, 5.2, 5.3, 5.4_

  - [x] 3.4 Write property test for progress bar visibility signal
    - **Property 1: Progress bar visibility is determined solely by active tab scan state**
    - **Validates: Requirements 1.1, 1.2, 1.3, 1.4, 5.1, 5.2, 5.3, 5.4**

  - [x] 3.5 Write property test for scroll-info response parsing
    - **Property 3: Scroll-info response parsing stores progress**
    - **Validates: Requirements 3.2**

- [x] 4. Checkpoint - Frontend state and parsing
  - Ensure all tests pass, ask the user if questions arise.

- [x] 5. Frontend: Render progress bar in status bar component
  - [x] 5.1 Update `status-bar.component.ts` to expose `isScanning` and `activeScanProgress` signals
    - Inject signals from `ShellStateService`
    - Expose as readonly properties for template binding
    - _Requirements: 1.1, 5.1_

  - [x] 5.2 Update `status-bar.component.html` to render progress bar
    - Insert `<div class="progress-bar">` between `.file-path` and `.wrap-checkbox`
    - Conditionally render with `@if (isScanning())`
    - Inner `<div class="progress-fill" [style.width.%]="activeScanProgress()"></div>`
    - _Requirements: 2.1, 2.5, 3.1_

  - [x] 5.3 Add `.progress-bar` and `.progress-fill` CSS styles to `status-bar.component.css`
    - `.progress-bar`: `flex-grow: 1; flex-shrink: 1; min-width: 0; height: 4px; background: #e0e0e0; border-radius: 2px; margin: 0 8px;`
    - `.progress-fill`: `height: 100%; background: #4a90d9; border-radius: 2px; transition: width 200ms ease;`
    - Ensure status bar outer height unchanged (max 16px bar height constraint met by 4px)
    - _Requirements: 2.2, 2.3, 2.4, 6.1, 6.2, 6.3, 6.4, 6.5_

  - [x] 5.4 Write property test for fill width equals progress percentage
    - **Property 2: Fill width equals progress percentage**
    - **Validates: Requirements 3.1**

  - [x] 5.5 Write unit tests for StatusBarComponent progress bar rendering
    - Test progress bar renders only when `isScanning()` is true
    - Test progress bar hidden when `isScanning()` is false
    - Test DOM order: file-path → progress-bar → wrap-checkbox
    - Test CSS classes applied (`.progress-bar`, `.progress-fill`)
    - _Requirements: 1.1, 1.2, 2.5_

- [x] 6. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- Backend uses `volatile long` + `Volatile.Read` for thread-safe `BytesRead` access (no lock needed)
- Frontend parsing is backward-compatible: 4-field responses leave `scanProgress` unchanged

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "3.1"] },
    { "id": 1, "tasks": ["1.2", "3.2"] },
    { "id": 2, "tasks": ["1.3", "3.3"] },
    { "id": 3, "tasks": ["1.4", "1.5", "3.4", "3.5"] },
    { "id": 4, "tasks": ["5.1"] },
    { "id": 5, "tasks": ["5.2", "5.3"] },
    { "id": 6, "tasks": ["5.4", "5.5"] }
  ]
}
```
