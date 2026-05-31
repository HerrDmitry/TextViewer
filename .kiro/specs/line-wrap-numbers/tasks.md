# Implementation Plan: Line Wrap & Line Numbers

## Overview

This plan implements line number gutter display and wrap mode (hard wrap at column boundary) for the text viewer. The implementation proceeds in layers: pure utility functions first, then backend wrapped extraction, then frontend state/rendering integration, and finally wiring everything together with scrolling and scrollbar logic.

## Tasks

- [x] 1. Implement pure utility functions for gutter and wrap computations
  - [x] 1.1 Create pure functions module with computeGutterWidth, computeNonWrappedLineNumbers, computeColCount
    - Create a new file `ClientApp/src/app/shell/line-wrap-utils.ts`
    - Implement `computeGutterWidth(totalLogicalLines, charWidth)` — returns `digits * charWidth + 16` or 0 if totalLogicalLines ≤ 0
    - Implement `computeNonWrappedLineNumbers(startLine, rowCount)` — returns array of 1-based line numbers
    - Implement `computeColCount(availablePixelWidth, gutterWidth, charWidth)` — returns `max(1, floor((width - gutter) / charWidth))`
    - _Requirements: 1.1, 1.4, 2.1, 9.1, 9.3, 9.4_

  - [x] 1.2 Write property tests for computeGutterWidth (Property 2)
    - **Property 2: Gutter width computation**
    - **Validates: Requirements 1.4**
    - Create `ClientApp/src/app/shell/shell-state.line-numbers.property.spec.ts`
    - Test that for any totalLogicalLines ≥ 1 and charWidth > 0, result equals `max(1, floor(log10(totalLines)) + 1) * charWidth + 16`
    - Use fast-check with `{ numRuns: 10 }`

  - [x] 1.3 Write property tests for computeNonWrappedLineNumbers (Property 1)
    - **Property 1: Non-wrapped line number computation**
    - **Validates: Requirements 1.1, 1.5, 1.7, 2.1**
    - Add to `shell-state.line-numbers.property.spec.ts`
    - Test that array length equals rowCount and each element at index i equals startLine + i + 1

  - [x] 1.4 Write property tests for computeColCount (Property 10)
    - **Property 10: Col_Count computation with gutter**
    - **Validates: Requirements 9.1, 9.3, 9.4**
    - Add to `shell-state.line-numbers.property.spec.ts`
    - Test that result equals `max(1, floor((pixelWidth - gutterWidth) / charWidth))`

  - [x] 1.5 Implement splitIntoVisualRows and computeWrappedScrollbarMax pure functions
    - Add to `line-wrap-utils.ts`
    - Implement `splitIntoVisualRows(content, colCount)` — splits at Col_Count boundaries, consumes newline delimiters as line-boundary markers
    - Implement `computeWrappedScrollbarMax(lineLengths, colCount)` — sum of ceil(len/colCount) for len > 0, plus 1 for len = 0
    - _Requirements: 7.1, 7.4, 7.5, 7.6_

  - [x] 1.6 Write property tests for splitIntoVisualRows (Property 8)
    - **Property 8: Response content splitting into visual rows**
    - **Validates: Requirements 7.1, 7.5, 7.6**
    - Create `ClientApp/src/app/shell/shell-state.wrap-mode.property.spec.ts`
    - Test: every row length ≤ colCount, no row contains newlines, empty content → zero rows

  - [x] 1.7 Write property tests for computeWrappedScrollbarMax (Property 9)
    - **Property 9: Wrapped-mode Scrollbar_Max computation**
    - **Validates: Requirements 7.4**
    - Add to `shell-state.wrap-mode.property.spec.ts`
    - Test: result equals sum of ceil(len/colCount) for len > 0 plus 1 for len = 0

  - [x] 1.8 Implement computeWrappedGutterNumbers pure function
    - Add to `line-wrap-utils.ts`
    - Implement `computeWrappedGutterNumbers(content, colCount, startLine, characterOffset)` — assigns line number to topmost visible visual row of each logical line, null for continuation rows
    - _Requirements: 3.1, 3.2, 3.3, 3.4_

  - [x] 1.9 Write property tests for computeWrappedGutterNumbers (Property 3)
    - **Property 3: Wrapped-mode gutter number placement**
    - **Validates: Requirements 3.1, 3.2, 3.3**
    - Add to `shell-state.line-numbers.property.spec.ts`
    - Test: exactly one non-null entry per logical line visible, placed on topmost visible row

- [x] 2. Implement backend GetWrappedViewAsync
  - [x] 2.1 Add GetWrappedViewAsync method to FileViewService.cs
    - Implement character-count-based extraction: reads from startLine at characterOffset, collects up to characterCount content characters
    - Newline delimiters NOT counted toward characterCount but included in output
    - Handle offset overflow by advancing to subsequent lines
    - Parameter validation: startLine < 0, characterOffset < 0, characterCount < 1 → error
    - Return empty string if startLine beyond file or scan in progress beyond scanned range
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 6.7, 6.8_

  - [x] 2.2 Update get-view handler in Program.cs to dispatch wrapped-mode requests
    - Detect wrapped mode by checking `fields[1] == "W"` when fields.Length == 5
    - Parse startLine, characterOffset, characterCount from fields[2..4]
    - Route to `GetWrappedViewAsync` for wrapped requests
    - Return `result.Error.Message` directly (already formatted with "ERROR:" prefix)
    - Existing rectangular handler remains unchanged for non-"W" payloads
    - _Requirements: 5.4, 6.1, 6.7_

  - [x] 2.3 Write property tests for GetWrappedViewAsync content-count invariant (Property 6)
    - **Property 6: Backend wrapped extraction content-count invariant**
    - **Validates: Requirements 6.1, 6.2, 6.3, 6.4, 6.5, 6.6**
    - C# xUnit + FsCheck test: for generated file content, response contains at most characterCount content chars (excluding delimiters), delimiters present at correct positions
    - Use `[Property(MaxTest = 10)]`

  - [x] 2.4 Write property tests for GetWrappedViewAsync parameter validation (Property 7)
    - **Property 7: Backend wrapped extraction parameter validation**
    - **Validates: Requirements 6.7**
    - C# xUnit + FsCheck test: invalid params → error string starting with "ERROR:" identifying first invalid param
    - Use `[Property(MaxTest = 10)]`

- [x] 3. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Extend frontend state for wrap mode
  - [x] 4.1 Extend TabViewState with characterOffset and needsRefresh fields
    - Add `characterOffset: number` (default 0) to `TabViewState` in `shell.types.ts`
    - Add `needsRefresh: boolean` (default false) to `TabViewState` in `shell.types.ts`
    - Update all TabViewState initialization sites to include new fields
    - _Requirements: 4.5, 5.1, 5.3, 8.1_

  - [x] 4.2 Add wrapMode signal and toggleWrapMode method to ShellStateService
    - Add `wrapMode = signal<boolean>(false)` to ShellStateService
    - Implement `toggleWrapMode()`: flip wrapMode, reset startCol to 0 for active tab, reset characterOffset to 0, mark non-active tabs needsRefresh, send appropriate view request
    - Handle no-active-tab case (update state only, no request)
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 4.6_

  - [x] 4.3 Implement sendWrappedViewRequest method in ShellStateService
    - Build payload: `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount`
    - Compute characterCount as colCount × rowCount, cap at 2,147,483,647
    - Cancel pending request before sending (latest-wins)
    - _Requirements: 5.1, 5.2, 5.4, 5.5_

  - [x] 4.4 Update handleViewResponse to split content into visual rows in wrapped mode
    - When wrapMode is on, use `splitIntoVisualRows` to split response content before storing in viewRows
    - Cache raw response content for gutter number computation
    - On error, preserve old rows and store error message
    - _Requirements: 7.1, 7.5, 4.7, 5.6_

  - [x] 4.5 Write property test for wrapped-mode request payload round-trip (Property 4)
    - **Property 4: Wrapped-mode request payload round-trip**
    - **Validates: Requirements 5.1, 5.2, 5.4**
    - Add to `shell-state.wrap-mode.property.spec.ts`
    - Test: encode then parse recovers original values; numeric fields contain only ASCII digits with no leading zeros

- [x] 5. Implement wrapped-mode scroll logic
  - [x] 5.1 Implement scrollDownOneVisualRow and scrollUpOneVisualRow methods
    - Add to ShellStateService (or as pure functions in line-wrap-utils.ts)
    - scrollDown: offset += colCount; if offset >= lineLen → next line, offset 0; boundary guard at last line
    - scrollUp: offset -= colCount; if negative → previous line's last wrapped row; boundary guard at line 0, offset 0
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5_

  - [x] 5.2 Implement scrollByVisualRows iterative method
    - Apply N steps of scrollDown/scrollUp iteratively
    - Stop early if boundary reached (atEnd/atTop)
    - Return final position and whether position changed
    - _Requirements: 8.6, 8.7_

  - [x] 5.3 Update handleWheel and handleArrowKey to use wrapped-mode scroll logic
    - When wrapMode is on: handleWheel calls scrollByVisualRows with ±WHEEL_STEP
    - When wrapMode is on: handleArrowKey calls scrollByVisualRows with ±ARROW_STEP
    - If positionChanged is false → no request sent (boundary guards)
    - If positionChanged is true → send wrapped-mode view request with new position
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.7_

  - [x] 5.4 Write property tests for scroll position round-trip (Property 5)
    - **Property 5: Wrapped-mode scroll position computation**
    - **Validates: Requirements 5.3, 8.1, 8.2, 8.3, 8.4, 8.5, 8.6**
    - Add to `shell-state.wrap-mode.property.spec.ts`
    - Test: scrollDown then scrollUp returns to original position (when not at boundary); scrollDown from non-terminal position either increases offset by colCount or advances line

- [x] 6. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Implement gutter and wrap UI rendering
  - [x] 7.1 Add gutter signals and computed properties to ShellStateService
    - Add `activeTotalLogicalLines` computed signal (from get-scroll-info lineCount)
    - Add `activeGutterWidth` computed signal using `computeGutterWidth`
    - Add `activeGutterNumbers` computed signal delegating to `computeNonWrappedLineNumbers` or `computeWrappedGutterNumbers` based on wrapMode
    - Store `charMetricsWidth` from measurement for gutter width calculation
    - _Requirements: 1.1, 1.4, 1.5, 3.1, 3.2, 3.3_

  - [x] 7.2 Update TextViewAreaComponent template for gutter and conditional horizontal scrollbar
    - Add `.line-number-gutter` div with `#gutterEl` reference and `[style.width.px]="gutterWidth()"`
    - Render gutter cells with `@for` loop over gutterNumbers signal
    - Conditionally hide horizontal scrollbar when wrapMode is on (`@if (!wrapMode())`)
    - _Requirements: 1.1, 1.2, 1.6, 2.3, 3.4, 7.3_

  - [x] 7.3 Add gutter CSS styles to text-view-area.component.css
    - `.line-number-gutter`: position absolute, top 0, left 0, overflow hidden, border-right, z-index 1, user-select none
    - `.gutter-cell`: monospace font, right-aligned, 8px padding each side, muted color
    - `.view-content`: margin-left adjusted by gutter width (CSS variable or inline style)
    - _Requirements: 1.2, 1.3_

  - [x] 7.4 Update TextViewAreaComponent measurement to subtract gutter width from Col_Count
    - In `measure()`, read `gutterEl.clientWidth` and subtract from available pixel width before computing colCount
    - Ensure minimum colCount of 1
    - Row_Count remains unchanged (gutter does not affect vertical measurement)
    - _Requirements: 9.1, 9.2, 9.3, 9.4_

  - [x] 7.5 Add Wrap checkbox to StatusBarComponent
    - Add `wrapMode` signal binding from ShellStateService
    - Add `onWrapToggle()` method calling `state.toggleWrapMode()`
    - Render checkbox with label "Wrap" in status-bar template
    - Style the checkbox in status-bar.component.css
    - _Requirements: 4.1, 4.2_

- [x] 8. Wire tab activation refresh and scrollbar updates for wrapped mode
  - [x] 8.1 Update activateTab to send wrapped-mode request for needsRefresh tabs
    - When activating a tab with `needsRefresh: true` and wrapMode is on, send wrapped-mode view request
    - When activating a tab with `needsRefresh: true` and wrapMode is off, send standard view request
    - Clear needsRefresh flag after sending request
    - _Requirements: 4.5, 5.5_

  - [x] 8.2 Update vertical scrollbar computation for wrapped mode
    - When wrapMode is on, Scrollbar_Max = total visual rows (from `computeWrappedScrollbarMax`)
    - Thumb position based on current visual row index / Scrollbar_Max
    - Implement get-line-lengths message for per-line char lengths (or compute from cached data)
    - _Requirements: 7.4, 8.1_

  - [x] 8.3 Update tryTriggerViewRequest to dispatch wrapped-mode request when wrapMode is on
    - When wrapMode is on and a view request is triggered (scan complete, dimensions change), send wrapped-mode request instead of standard
    - _Requirements: 5.5_

- [x] 9. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- The design uses TypeScript for frontend (Angular) and C# for backend (.NET 10)
- Property-based tests use `{ numRuns: 10 }` per workspace steering rule
- Pure functions are implemented first to enable early property testing before integration

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "2.1"] },
    { "id": 1, "tasks": ["1.2", "1.3", "1.4", "1.5", "2.2"] },
    { "id": 2, "tasks": ["1.6", "1.7", "1.8", "2.3", "2.4"] },
    { "id": 3, "tasks": ["1.9", "4.1"] },
    { "id": 4, "tasks": ["4.2", "4.3"] },
    { "id": 5, "tasks": ["4.4", "4.5", "5.1"] },
    { "id": 6, "tasks": ["5.2"] },
    { "id": 7, "tasks": ["5.3", "5.4"] },
    { "id": 8, "tasks": ["7.1", "7.5"] },
    { "id": 9, "tasks": ["7.2", "7.3", "7.4"] },
    { "id": 10, "tasks": ["8.1", "8.2", "8.3"] }
  ]
}
```
