# Implementation Plan: Text Handling

## Overview

Connect the UI Shell's TextViewAreaComponent to the backend FileViewService via the Message Bus. After a file is opened, the backend waits 500ms for the scan to make initial progress, calls GetViewAsync with viewport dimensions from the request, and returns an Initial_View alongside the View_Session_ID and file path. The frontend renders this Initial_View immediately. When the full scan completes, the backend pushes a single "scan-complete" notification, and the frontend sends a refresh "get-view" request to obtain fully-indexed content.

## Tasks

- [x] 1. Extend types and backend session infrastructure
  - [x] 1.1 Add TabViewState, ViewDimensions types and extend Tab interface
    - Add `viewSessionId: string` field to the `Tab` interface in `ClientApp/src/app/shell/shell.types.ts`
    - Add `TabViewState` interface with fields: `scanComplete`, `viewRows`, `errorMessage`, `pendingCorrelationId`, `deferred`
    - Add `ViewDimensions` interface with fields: `rowCount`, `colCount`
    - _Requirements: 7.2, 2.1, 1.3, 1.4_

  - [x] 1.2 Modify open-file handler in Program.cs to be async with Initial_View
    - Make the open-file handler async (change return from `Task.FromResult` to `async` lambda)
    - Parse viewport dimensions from the request payload: split payload on `\n`, extract rowCount and colCount; use fallback 40 rows / 120 cols if payload is empty or invalid
    - After creating FileViewService and storing in sessions, `await Task.Delay(500)` to let scan make initial progress
    - Call `service.GetViewAsync(0, 0, rowCount, colCount)` to get Initial_View rows
    - Strip line-ending delimiters from each row using `StripDelimiter`
    - Return response in format `viewSessionId\nfilePath\nrow1\nrow2\n...` (rows appended after filePath); if GetViewAsync fails or returns zero rows, return just `viewSessionId\nfilePath`
    - _Requirements: 8.1, 8.2, 8.3, 8.5, 7.1, 7.2_

  - [x] 1.3 Add get-view handler in Program.cs
    - Register `messageBus.RegisterHandler("get-view", ...)` that parses 5-field newline-delimited payload (viewSessionId, startLine, startCol, rowCount, colCount)
    - Validate field count (exactly 5 fields), validate each numeric field with `int.TryParse` and range checks (startLine ≥ 0, startCol ≥ 0, rowCount ≥ 1, colCount ≥ 1)
    - Look up FileViewService from sessions dictionary by viewSessionId; return `ERROR:Session not found: {viewSessionId}` if missing
    - Invoke `service.GetViewAsync(startLine, startCol, rowCount, colCount)` and return rows with line-ending delimiters stripped, joined by `\n`; return `ERROR:{message}` on ViewError
    - Add `StripDelimiter` static helper method
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 6.1, 6.2, 6.3, 6.4, 6.5, 6.6_

  - [x] 1.4 Add close-file handler in Program.cs
    - Register `messageBus.RegisterHandler("close-file", ...)` that disposes and removes the FileViewService from sessions by viewSessionId
    - Return `null` (fire-and-forget); no-op if viewSessionId not found
    - _Requirements: 7.5, 7.6_

  - [x] 1.5 Simplify MonitorScanState to only send at FullScanComplete
    - Remove the `quickSent` variable and the QuickScanComplete push logic
    - MonitorScanState should poll until `ScanState >= FullScanComplete`, send a single "scan-complete" push, then exit
    - Also break on `ScanState.Failed`
    - _Requirements: 3.1, 3.2_

- [x] 2. Checkpoint - Verify backend handlers compile
  - Ensure `dotnet build` succeeds, ask the user if questions arise.

- [x] 3. Implement frontend view request orchestration in ShellStateService
  - [x] 3.1 Extend ShellStateService with view state signals and scan-complete subscription
    - Add `tabViewStates` signal (`Map<string, TabViewState>`), `viewDimensions` signal (`ViewDimensions | null`)
    - Add computed signals: `activeViewRows`, `activeViewError`, `isViewPending`
    - Subscribe to "scan-complete" messages on MessageBusClient with accumulate queue mode (configure before subscribe)
    - Handle scan-complete: match viewSessionId to tab, update `scanComplete` in TabViewState, call `tryTriggerViewRequest()`
    - Discard scan-complete for unknown sessions silently
    - _Requirements: 3.3, 3.4, 3.5, 2.1, 2.2_

  - [x] 3.2 Implement view request orchestration logic in ShellStateService
    - Add `updateViewDimensions(dims: ViewDimensions)` action method called by TextViewAreaComponent
    - Add private `tryTriggerViewRequest()` that checks: active tab exists + scanComplete for that session + dimensions available + no pending request → sends "get-view" message with payload `viewSessionId\n0\n0\nrowCount\ncolCount`
    - Handle deferred requests: set `deferred=true` if scan-complete but no dimensions yet; trigger on dimension arrival
    - Cancel deferred when active tab changes before measurement completes
    - On resize (dimensions change): re-trigger get-view for active tab if scanComplete
    - Enforce duplicate suppression: don't send if `pendingCorrelationId` is non-null for that tab
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6_

  - [x] 3.3 Implement view response handling and tab lifecycle in ShellStateService
    - Subscribe to "get-view" responses: on correlated response, parse payload — if starts with "ERROR:" store in `errorMessage`, else split by `\n` and store in `viewRows`; clear `pendingCorrelationId`
    - Modify `closeTab()`: cancel pending request (call `messageBus.cancel()`), send "close-file" message with viewSessionId, remove TabViewState entry
    - Cancel pending/deferred on tab close per requirement 2.7
    - _Requirements: 5.1, 5.4, 5.5, 2.7, 7.7_

  - [x] 3.4 Modify triggerOpenFile() to include viewport dimensions in payload
    - Change `triggerOpenFile()` to read current `viewDimensions()` signal
    - Format payload as `rowCount\ncolCount` (e.g. `"40\n120"`)
    - Use fallback values 40 rows / 120 cols if `viewDimensions()` is null
    - Pass payload to `this.messageBus.send('open-file', payload)`
    - _Requirements: 8.2, 8.6_

  - [x] 3.5 Modify open-file response parsing to handle Initial_View rows
    - Update the open-file subscription handler to parse the new 3-part format: `viewSessionId\nfilePath\nrow1\nrow2\n...`
    - Split on first `\n` to get viewSessionId, then split remainder on second `\n` to get filePath and row data
    - If row data exists (content after second newline), split by `\n` and store as `initialRows`
    - When creating the initial `TabViewState`, set `viewRows: initialRows` (instead of `null`) so rows render immediately
    - Maintain backward compat: if no newline in payload, treat entire payload as filePath
    - _Requirements: 8.3, 8.4, 7.2, 2.1_

  - [x] 3.6 Write property test: Dimension computation correctness (Property 1)
    - **Property 1: Dimension computation correctness**
    - **Validates: Requirements 1.3, 1.4**
    - Test file: `ClientApp/src/app/shell/text-handling.property.spec.ts`
    - Generate random positive pixel W/H (1–5000) and char W/H (1–100), assert rowCount = max(1, floor(H/CH)) and colCount = max(1, floor(W/CW))
    - Use fast-check with `{ numRuns: 10 }`

  - [x] 3.7 Write property test: View request orchestration invariant (Property 2)
    - **Property 2: View request orchestration invariant**
    - **Validates: Requirements 2.1, 2.2, 2.4, 2.5, 2.6, 2.7, 3.3, 8.4**
    - Test file: `ClientApp/src/app/shell/text-handling.property.spec.ts`
    - Generate random event sequences from {activateTab, scanComplete, measureComplete, resize, closeTab, openFileResponse} (length 1–20)
    - Assert: refresh request sent iff scanComplete + dimensions; Initial_View rendered immediately; at most 1 pending per tab; cancel on close
    - Use fast-check with `{ numRuns: 10 }`

  - [x] 3.8 Write property test: Payload format round-trip (Property 3)
    - **Property 3: Payload format round-trip**
    - **Validates: Requirements 4.1, 6.1, 6.2**
    - Test file: `ClientApp/src/app/shell/text-handling.property.spec.ts`
    - Generate random UUIDs (no newlines), random ints 0–2^31-1 for numeric fields
    - Assert: encode → parse recovers original values exactly
    - Use fast-check with `{ numRuns: 10 }`

  - [x] 3.9 Write property test: Open-file response format round-trip (Property 7)
    - **Property 7: Open-file response format round-trip**
    - **Validates: Requirements 7.2, 8.3, 8.4**
    - Test file: `ClientApp/src/app/shell/text-handling.property.spec.ts`
    - Generate random viewSessionId, filePath (no newlines), random row arrays (0–50 rows, no newlines in rows)
    - Assert: encode as `viewSessionId\nfilePath\nrow1\nrow2\n...` → parse recovers viewSessionId, filePath, and rows exactly; empty rows → `viewSessionId\nfilePath`
    - Use fast-check with `{ numRuns: 10 }`

  - [x] 3.10 Write property test: Open-file request payload round-trip (Property 8)
    - **Property 8: Open-file request payload round-trip**
    - **Validates: Requirements 8.2, 8.6**
    - Test file: `ClientApp/src/app/shell/text-handling.property.spec.ts`
    - Generate random rowCount (1–2^31-1), colCount (1–2^31-1)
    - Assert: encode as `rowCount\ncolCount` → parse recovers values; empty/malformed → fallback (40, 120)
    - Use fast-check with `{ numRuns: 10 }`

- [x] 4. Implement TextViewAreaComponent measurement and rendering
  - [x] 4.1 Add ResizeObserver-based measurement pipeline to TextViewAreaComponent
    - Implement `AfterViewInit` and `OnDestroy` lifecycle hooks
    - Inject `ElementRef` for DOM access
    - Set up `ResizeObserver` on host element in `ngAfterViewInit`; debounce with 150ms `setTimeout`
    - Implement `measure()`: get `clientWidth`/`clientHeight`, call `computeCharMetrics()`, compute rowCount = max(1, floor(height/charHeight)), colCount = max(1, floor(width/charWidth))
    - Implement `computeCharMetrics()`: create off-screen span with same font-family/font-size as `.view-row`, set textContent="M", measure `getBoundingClientRect()`, remove span, return {width, height}; use fallback (8px width, 16px height) if measurement returns 0
    - Call `state.updateViewDimensions()` when dimensions change
    - Skip measurement when no active tab exists
    - Clean up ResizeObserver and debounce timer in `ngOnDestroy`
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7_

  - [x] 4.2 Update TextViewAreaComponent template and styles for row rendering
    - Update template to display: empty-state (no tabs), error message (viewError), view rows (iterate with `@for`), or empty content (pending, no cache)
    - Each row rendered as a block-level `div.view-row` element with monospace font
    - Set `overflow: hidden` on content container
    - Add `.view-error` CSS class for visually distinct error display
    - Wire signals: `viewRows` from `state.activeViewRows`, `viewError` from `state.activeViewError`, `isViewPending` from `state.isViewPending`
    - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6_

  - [x] 4.3 Write property test: Response encoding correctness (Property 4)
    - **Property 4: Response encoding correctness**
    - **Validates: Requirements 4.4, 4.5, 6.5, 6.6**
    - Test file: `ClientApp/src/app/shell/text-handling.property.spec.ts`
    - Generate random string arrays (0–50 rows, each 0–200 chars with random line endings \n, \r\n, \r)
    - Assert: strip delimiters + join by \n matches expected output; error responses start with "ERROR:"
    - Use fast-check with `{ numRuns: 10 }`

  - [x] 4.4 Write property test: Payload parse error identification (Property 5)
    - **Property 5: Payload parse error identification**
    - **Validates: Requirements 4.6, 6.3, 6.4**
    - Test file: `ClientApp/src/app/shell/text-handling.property.spec.ts`
    - Generate random malformed payloads (wrong field count, non-digits, leading zeros, out-of-range values)
    - Assert: error response starts with "ERROR:" and identifies the specific failure
    - Use fast-check with `{ numRuns: 10 }`

- [x] 5. Checkpoint - Verify build after Requirement 8 changes
  - Ensure `dotnet build` succeeds and `npx ng build` succeeds in ClientApp, ask the user if questions arise.

- [x] 6. Backend property tests and session lifecycle
  - [x] 6.1 Write property test: Session lifecycle invariant (Property 6)
    - **Property 6: Session lifecycle invariant**
    - **Validates: Requirements 7.1, 7.3, 7.5**
    - Backend test file using xUnit + FsCheck with `[Property(MaxTest = 10)]`
    - Generate random sequences of {open, close, getView} operations (length 1–15)
    - Assert: unique IDs per open, correct lookup, dispose on close, error for closed/unknown sessions

  - [x] 6.2 Write unit tests for backend handlers
    - Test get-view with valid payload → correct GetViewAsync params
    - Test get-view with wrong field count → ERROR response
    - Test get-view with non-integer field → ERROR identifies field name
    - Test get-view with unknown session → ERROR session not found
    - Test open-file creates session, waits 500ms, calls GetViewAsync, returns `viewSessionId\nfilePath\nrows...`
    - Test open-file with empty payload uses fallback dimensions (40×120)
    - Test open-file with valid payload parses rowCount and colCount
    - Test close-file disposes service and removes from map
    - Test close-file with unknown session → no-op
    - Test scan-complete sent only at FullScanComplete (not QuickScanComplete)
    - Test multiple opens of same file → independent sessions
    - _Requirements: 4.1–4.6, 6.1–6.6, 7.1, 7.3, 7.5, 7.6, 3.1, 3.2, 8.1, 8.2, 8.5_

- [x] 7. Frontend unit tests
  - [x] 7.1 Write unit tests for ShellStateService text-handling extensions
    - Test scan-complete subscription configured with accumulate queue mode
    - Test scan-complete for unknown session discarded
    - Test open-file response parsed: viewSessionId + filePath + Initial_View rows
    - Test open-file response with no rows: viewSessionId + filePath only
    - Test triggerOpenFile sends viewport dimensions in payload (rowCount\ncolCount)
    - Test triggerOpenFile uses fallback 40×120 when viewDimensions is null
    - Test Initial_View rows stored in TabViewState.viewRows immediately on open
    - Test close-file sent on tab close with viewSessionId
    - Test deferred request triggered when measurement completes
    - Test duplicate suppression (no send while pending)
    - Test cancel on tab close
    - _Requirements: 3.3, 3.4, 3.5, 7.2, 7.7, 2.4, 2.5, 2.7, 8.4, 8.6_

  - [x] 7.2 Write unit tests for TextViewAreaComponent measurement and rendering
    - Test measurement skipped when no active tab
    - Test measurement triggered on active tab (AfterViewInit)
    - Test Char_Metrics uses "M" reference character
    - Test resize debounce: multiple events within 150ms → single recompute
    - Test dimension change emits new values
    - Test view rows rendered as block elements in order
    - Test overflow hidden on content container
    - Test error response displayed in distinct style
    - Test tab switch shows cached rows synchronously
    - Test pending state with no cache shows empty content
    - _Requirements: 1.1, 1.2, 1.5, 1.6, 1.7, 5.1, 5.2, 5.3, 5.4, 5.5, 5.6_

- [x] 8. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- Frontend uses TypeScript/Angular with fast-check for property tests (`{ numRuns: 10 }`)
- Backend uses C#/.NET with xUnit + FsCheck for property tests (`[Property(MaxTest = 10)]`)
- The open-file response format is now `viewSessionId\nfilePath\nrow1\nrow2\n...` — Initial_View rows included
- The open-file request payload is now `rowCount\ncolCount` — viewport dimensions sent to backend
- MonitorScanState only sends scan-complete at FullScanComplete (QuickScanComplete push removed)
- Tasks 1.2, 1.5, 3.4, 3.5 are the new tasks implementing Requirement 8 changes

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.2", "1.5"] },
    { "id": 1, "tasks": ["3.4", "3.5"] },
    { "id": 2, "tasks": ["3.6", "3.7", "3.8", "3.9", "3.10"] },
    { "id": 3, "tasks": ["4.3", "4.4", "6.1", "6.2"] },
    { "id": 4, "tasks": ["7.1", "7.2"] }
  ]
}
```
