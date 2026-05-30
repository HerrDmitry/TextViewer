# Implementation Plan: Scroll Navigation

## Overview

Implement interactive scroll navigation for the TextViewer app. This adds thumb dragging, mouse wheel scrolling, arrow key navigation, proportional thumb sizing/positioning, and latest-wins view request cancellation. All scroll state logic lives in `ShellStateService`; `TextViewAreaComponent` forwards DOM events and renders computed styles.

## Tasks

- [x] 1. Extend data model and add scroll state infrastructure
  - [x] 1.1 Add `startLine`, `startCol` to `TabViewState` and create `DragState` interface in `shell.types.ts`
    - Add `startLine: number` and `startCol: number` fields to `TabViewState` interface (default 0)
    - Create `DragState` interface with fields: `axis`, `startMousePos`, `startScrollPos`, `trackLength`, `scrollbarMax`, `viewportSize`
    - Update all `TabViewState` construction sites in `shell-state.service.ts` to include `startLine: 0, startCol: 0`
    - _Requirements: 1.1, 2.1, 7.4_

  - [x] 1.2 Add scroll constants and `clamp` utility to `ShellStateService`
    - Add `WHEEL_STEP = 3`, `ARROW_STEP = 1`, `MIN_THUMB_SIZE = 20` constants
    - Add private `clamp(value, min, max)` helper function
    - Add `dragState` signal initialized to `null`
    - Add `updateScrollPosition` private method to update `startLine`/`startCol` in `TabViewState`
    - _Requirements: 3.1, 4.1_

- [x] 2. Implement thumb size and position computed signals
  - [x] 2.1 Add thumb ratio and fraction computed signals to `ShellStateService`
    - Implement `verticalThumbRatio` computed signal: `rowCount / verticalMax` (returns 1 when all content fits)
    - Implement `horizontalThumbRatio` computed signal: `colCount / horizontalMax` (returns 1 when all content fits)
    - Implement `verticalThumbFraction` computed signal: `startLine / (verticalMax - rowCount)` (returns 0 when non-scrollable)
    - Implement `horizontalThumbFraction` computed signal: `startCol / (horizontalMax - colCount)` (returns 0 when non-scrollable)
    - _Requirements: 5.1, 5.2, 5.4, 5.5, 5.6, 6.1, 6.2, 6.4_

  - [x] 2.2 Write property test for thumb position fraction (Property 4)
    - **Property 4: Thumb position fraction is proportional to scroll position**
    - For any startLine in [0, scrollbarMax - rowCount] where scrollbarMax > rowCount, fraction = startLine / (scrollbarMax - rowCount), result in [0, 1]
    - **Validates: Requirements 5.1, 5.2, 5.4, 5.5**

  - [x] 2.3 Write property test for thumb size ratio (Property 5)
    - **Property 5: Thumb size ratio is proportional to viewport coverage**
    - For any viewportSize > 0 and scrollbarMax > viewportSize, ratio = viewportSize / scrollbarMax, result in (0, 1). When converted to pixels (ratio × trackPixelSize), result ≥ 20px.
    - **Validates: Requirements 6.1, 6.2**

- [x] 3. Implement scroll action methods in ShellStateService
  - [x] 3.1 Implement `handleWheel(deltaY, deltaX)` method
    - Compute new `startLine` using `clamp(current + sign(deltaY) * 3, 0, verticalMax - rowCount)`
    - Compute new `startCol` using `clamp(current + sign(deltaX) * 3, 0, horizontalMax - colCount)`
    - Skip if both unchanged (already at boundary)
    - Guard: no-op if `scrollbarMax <= viewportSize` for the respective axis
    - Call `updateScrollPosition` then `sendScrollViewRequest`
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.6, 3.7_

  - [x] 3.2 Implement `handleArrowKey(direction)` method
    - Compute new position using step of 1 in the given direction
    - Clamp to `[0, scrollbarMax - viewportSize]`
    - Skip if unchanged (at boundary)
    - Guard: no-op if no active tab or `scrollbarMax <= viewportSize`
    - Call `updateScrollPosition` then `sendScrollViewRequest`
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.8_

  - [x] 3.3 Implement drag methods: `handleVerticalDragStart`, `handleHorizontalDragStart`, `handleDragMove`, `handleDragEnd`
    - `handleVerticalDragStart`: guard non-interactive, capture DragState with axis='vertical'
    - `handleHorizontalDragStart`: guard non-interactive, capture DragState with axis='horizontal'
    - `handleDragMove`: compute `clamp(startScrollPos + round(delta / trackLength * maxScroll), 0, maxScroll)`, update position optimistically
    - `handleDragEnd`: clear dragState, send view request with final position
    - Guard: reject drag start if `trackLength <= 0` (division by zero prevention)
    - _Requirements: 1.1, 1.2, 1.4, 1.5, 2.1, 2.2, 2.4, 2.5_

  - [x] 3.4 Write property test for scroll step clamping (Property 1)
    - **Property 1: Scroll step computation is clamped to valid range**
    - For any current position, step (1 or 3), direction (+/-), scrollbarMax, viewportSize where scrollbarMax > viewportSize: result = clamp(current + sign * step, 0, scrollbarMax - viewportSize), result always in [0, scrollbarMax - viewportSize]
    - **Validates: Requirements 3.1, 3.2, 4.1, 4.2, 4.3, 4.4**

  - [x] 3.5 Write property test for drag position clamping (Property 2)
    - **Property 2: Drag position computation is clamped to valid range**
    - For any DragState with trackLength > 0, scrollbarMax > viewportSize, and any mouse delta: result = clamp(startScrollPos + round(delta / trackLength * (scrollbarMax - viewportSize)), 0, scrollbarMax - viewportSize), result always in [0, scrollbarMax - viewportSize]
    - **Validates: Requirements 1.2, 2.2**

  - [x] 3.6 Write property test for non-interactive guard (Property 3)
    - **Property 3: Non-interactive when content fits viewport**
    - For any scrollbarMax ≤ viewportSize: drag start produces no DragState, wheel/arrow produce no position change, thumb fraction = 0, thumb ratio = 1
    - **Validates: Requirements 1.5, 2.5, 3.6, 3.7, 5.6, 6.4**

- [x] 4. Implement latest-wins view request cancellation
  - [x] 4.1 Implement `sendScrollViewRequest` private method with latest-wins cancellation
    - If `pendingCorrelationId` is non-null for the tab, call `messageBus.cancel(pendingCorrelationId)` before sending
    - Build payload: `viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount`
    - Store new `pendingCorrelationId` in `TabViewState`
    - _Requirements: 7.1, 7.2_

  - [x] 4.2 Update `handleViewResponse` to handle scroll-triggered responses
    - On success: replace `viewRows` with new rows, clear `pendingCorrelationId`
    - On error: keep previous `viewRows` visible, store error in `errorMessage`, clear `pendingCorrelationId`
    - Recompute thumb position via existing computed signals (automatic from startLine/startCol update)
    - _Requirements: 7.3, 8.1, 8.2, 8.3, 8.4, 5.3_

- [x] 5. Checkpoint
  - Ensure all tests pass, ask the user if questions arise.

- [x] 6. Wire DOM events in TextViewAreaComponent
  - [x] 6.1 Add template references, event bindings, and thumb inline styles to `text-view-area.component.html`
    - Add `#verticalTrack` and `#horizontalTrack` template references on scrollbar track elements
    - Add `(mousedown)="onVerticalThumbMousedown($event)"` on vertical thumb
    - Add `(mousedown)="onHorizontalThumbMousedown($event)"` on horizontal thumb
    - Add `(wheel)="onWheel($event)"` on the host `.text-view-area` div
    - Add `(keydown)="onKeydown($event)"` on the host div
    - Add `tabindex="0"` on the host div for keyboard focus
    - Bind `[style.height.px]` and `[style.top.px]` on vertical thumb
    - Bind `[style.width.px]` and `[style.left.px]` on horizontal thumb
    - _Requirements: 1.1, 2.1, 3.5, 4.7, 6.5_

  - [x] 6.2 Implement component event handler methods in `text-view-area.component.ts`
    - Add `@ViewChild('verticalTrack')` and `@ViewChild('horizontalTrack')` references
    - Implement `onWheel(event)`: call `preventDefault()`, extract `deltaY`/`deltaX`, call `state.handleWheel()`
    - Implement `onKeydown(event)`: guard no active tab (return without preventDefault), otherwise `preventDefault()` and call `state.handleArrowKey()`
    - Implement `onVerticalThumbMousedown(event)`: compute trackLength, apply `user-select: none`, call `state.handleVerticalDragStart()`, attach document-level `mousemove`/`mouseup` listeners, clean up on mouseup
    - Implement `onHorizontalThumbMousedown(event)`: same pattern for horizontal axis
    - Implement `computeVerticalThumbPx()` and `computeHorizontalThumbPx()`: convert ratio to pixels with 20px minimum
    - Implement `computeVerticalThumbTopPx()` and `computeHorizontalThumbLeftPx()`: convert fraction to pixel offset
    - Expose `verticalThumbRatio`, `verticalThumbFraction`, `horizontalThumbRatio`, `horizontalThumbFraction` from service
    - _Requirements: 1.1, 1.3, 1.6, 2.1, 2.3, 2.6, 3.5, 4.7, 4.8, 6.5_

  - [x] 6.3 Update CSS to remove placeholder thumb sizes and support dynamic inline styles
    - Remove `height: 40px` placeholder from `.scrollbar-vertical .scrollbar-thumb`
    - Remove `width: 40px` placeholder from `.scrollbar-horizontal .scrollbar-thumb`
    - Keep `min-height: 20px` and `min-width: 20px` as CSS fallback
    - Add `cursor: grab` on `.scrollbar-thumb` and `cursor: grabbing` on `.scrollbar-thumb:active`
    - _Requirements: 6.5, 1.3, 2.3_

- [x] 7. Restore scroll position on tab switch
  - [x] 7.1 Update `activateTab` to restore thumb position from stored `startLine`/`startCol`
    - When switching tabs, the computed signals (`verticalThumbFraction`, `horizontalThumbFraction`) automatically reflect the stored `startLine`/`startCol` from `TabViewState` — verify no extra logic needed
    - Ensure no new view request is sent on tab switch when cached rows exist (existing behavior from text-handling Req 5.5)
    - _Requirements: 7.4, 7.5_

- [x] 8. Checkpoint
  - Ensure all tests pass, ask the user if questions arise.

- [x] 9. Unit tests for scroll navigation
  - [x] 9.1 Write unit tests for ShellStateService scroll methods
    - Test: drag start captures initial state correctly (Req 1.1, 2.1)
    - Test: drag end clears state and sends view request (Req 1.4, 2.4)
    - Test: no view request when position unchanged at boundary (Req 3.4, 4.6)
    - Test: arrow keys ignored when no active tab (Req 4.8)
    - Test: latest-wins cancellation — pending request cancelled on new scroll (Req 7.2)
    - Test: view response updates displayed rows (Req 7.3, 8.1)
    - Test: previous rows preserved while pending (Req 8.3)
    - Test: error response keeps rows, shows error (Req 8.4)
    - Test: tab switch restores thumb position without new request (Req 7.5)
    - Test: thumb position recomputed on view response (Req 5.3)
    - _Requirements: 1.1, 1.4, 2.1, 2.4, 3.4, 4.6, 4.8, 5.3, 7.2, 7.3, 7.5, 8.1, 8.3, 8.4_

  - [x] 9.2 Write unit tests for TextViewAreaComponent event handlers
    - Test: wheel preventDefault called (Req 3.5)
    - Test: arrow key preventDefault when active tab exists (Req 4.7)
    - Test: user-select: none applied during drag, removed after (Req 1.6, 2.6)
    - Test: thumb size applied as inline style (Req 6.5)
    - _Requirements: 1.6, 2.6, 3.5, 4.7, 6.5_

- [x] 10. Final checkpoint
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document (fast-check, 10 iterations per steering rule)
- Unit tests validate specific examples and edge cases
- All scroll logic lives in `ShellStateService`; the component only forwards DOM events
- The design uses TypeScript (Angular) — no language selection needed

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["1.2"] },
    { "id": 2, "tasks": ["2.1", "3.1", "3.2", "3.3"] },
    { "id": 3, "tasks": ["2.2", "2.3", "3.4", "3.5", "3.6", "4.1"] },
    { "id": 4, "tasks": ["4.2"] },
    { "id": 5, "tasks": ["6.1", "6.2", "6.3", "7.1"] },
    { "id": 6, "tasks": ["9.1", "9.2"] }
  ]
}
```
