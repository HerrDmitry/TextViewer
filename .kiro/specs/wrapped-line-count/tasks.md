# Implementation Plan: wrapped-line-count

## Overview

Replace bulk `get-line-lengths` with single-integer `get-wrapped-line-count` backend handler, add backend visual row index resolution, remove all `lineLengths` signal infrastructure from frontend, and add per-session caching. Backend in C#, frontend in TypeScript/Angular.

## Tasks

- [x] 1. Backend: Add `get-wrapped-line-count` handler and cache
  - [x] 1.1 Implement `ComputeWrappedLineCount` static method in Program.cs
    - Add `internal static long ComputeWrappedLineCount(LineIndex lineIndex, int lineCount, int colCount)` using `Parallel.For` with thread-local accumulators and `Interlocked.Add`
    - Each line: if charLen is null, fall back to byte length; if length == 0 → 1 visual row; else `ceil(len / colCount)`
    - _Requirements: 1.2, 1.3, 1.4, 2.1, 2.2_

  - [x] 1.2 Implement `HandleGetWrappedLineCount` static method in Program.cs
    - Parse payload `{sessionId}\n{colCount}`, validate session existence first, validate colCount >= 1
    - Declare `wrappedLineCountCache` dictionary at class/Main scope: `Dictionary<string, (int colCount, int lineCount, long total)>`
    - Check cache: if `(sessionId, colCount, lineCount)` matches → return cached total
    - Otherwise call `ComputeWrappedLineCount`, store result in cache, return as string
    - Return `ERROR:` prefixed strings for invalid payload, missing session, or colCount < 1
    - _Requirements: 1.1, 1.5, 1.6, 6.1, 6.2, 6.3, 6.4_

  - [x] 1.3 Register `get-wrapped-line-count` handler in Program.Main
    - Add `messageBus.RegisterHandler("get-wrapped-line-count", ...)` calling `HandleGetWrappedLineCount`
    - Pass `sessions`, `sessionLock`, and `wrappedLineCountCache` to handler
    - _Requirements: 1.1_

  - [x] 1.4 Write property test for `ComputeWrappedLineCount` (C# / FsCheck)
    - **Property 1: Wrapped line count computation correctness**
    - Generate random int[] line lengths + random colCount >= 1, verify result matches sequential sum of ceil(len/colCount) with zero-length → 1
    - `[Property(MaxTest = 10)]`
    - **Validates: Requirements 1.1, 1.2, 1.3, 2.2**

  - [x] 1.5 Write property test for char-length fallback (C# / FsCheck)
    - **Property 4: Char-length fallback**
    - Generate LineIndex mock with mixed null/non-null char lengths, verify fallback to byte length produces correct result
    - `[Property(MaxTest = 10)]`
    - **Validates: Requirements 1.4**

- [x] 2. Backend: Add visual row index resolution
  - [x] 2.1 Implement `ResolveVisualRowIndex` static method in Program.cs
    - `internal static (int startLine, int characterOffset) ResolveVisualRowIndex(LineIndex lineIndex, int lineCount, int colCount, long visualRowIndex)`
    - Iterate lines summing visual rows until cumulative sum exceeds target; return (line, rowWithinLine * colCount)
    - Clamp to last visual row when index exceeds total; return (0, 0) when lineCount == 0 or visualRowIndex == 0
    - _Requirements: 4.1, 4.2, 4.3, 4.4_

  - [x] 2.2 Integrate `ResolveVisualRowIndex` into `HandleGetView` wrapped-mode branch
    - When wrapped-mode request arrives, call `ResolveVisualRowIndex` to convert visual row index to (startLine, characterOffset) before calling `GetWrappedViewAsync`
    - _Requirements: 4.1_

  - [x] 2.3 Write property test for `ResolveVisualRowIndex` (C# / FsCheck)
    - **Property 2: Visual row index resolution round-trip**
    - Generate random line lengths + random visual row index in [0, totalVisualRows), verify resolving and recomputing cumulative position equals original index
    - `[Property(MaxTest = 10)]`
    - **Validates: Requirements 4.1, 4.2, 4.3, 4.4**

- [x] 3. Checkpoint
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Backend: Cache eviction and removal of `get-line-lengths`
  - [x] 4.1 Add cache eviction on `close-file`
    - In `HandleCloseFile`, after `sessions.Remove`, call `wrappedLineCountCache.Remove(viewSessionId)`
    - _Requirements: 6.5_

  - [x] 4.2 Remove `get-line-lengths` handler registration and `HandleGetLineLengths` method
    - Remove `messageBus.RegisterHandler("get-line-lengths", ...)` from Program.Main
    - Remove `HandleGetLineLengths` static method entirely
    - _Requirements: 5.3_

  - [x] 4.3 Write property test for cache key correctness (C# / FsCheck)
    - **Property 3: Cache key correctness**
    - Generate sequences of (colCount, lineCount) pairs for same session, verify cache hit when both unchanged, miss when either changes
    - `[Property(MaxTest = 10)]`
    - **Validates: Requirements 6.1, 6.2, 6.3, 6.4**

- [x] 5. Frontend: Add `get-wrapped-line-count` subscription and handler
  - [x] 5.1 Add `wrappedLineCountSubscription` and `handleWrappedLineCountResponse` to ShellStateService
    - Subscribe to `get-wrapped-line-count` in constructor
    - Parse response: if starts with `ERROR:` or is not a valid non-negative integer → set verticalMax = 0; otherwise set verticalMax to parsed value
    - Call `updateTabScrollbar` with new verticalMax, preserving horizontalMax
    - Unsubscribe in `ngOnDestroy`
    - _Requirements: 3.4_

  - [x] 5.2 Add `requestWrappedLineCount` private method to ShellStateService
    - Build payload `${sessionId}\n${colCount}` from viewDimensions
    - Send via `messageBus.send('get-wrapped-line-count', payload)`
    - _Requirements: 3.1, 3.2, 3.3_

  - [x] 5.3 Wire `requestWrappedLineCount` into trigger points
    - `toggleWrapMode`: call `requestWrappedLineCount` instead of `requestLineLengths`
    - `handleScrollInfoResponse` on scan terminal state when wrapMode active: call `requestWrappedLineCount` instead of `requestLineLengths`
    - `activateTab` when wrap mode active: call `requestWrappedLineCount` instead of `requestLineLengths`
    - Viewport resize (existing 150ms debounce): call `requestWrappedLineCount` when wrap mode active
    - _Requirements: 3.1, 3.2, 3.3_

  - [x] 5.4 Write property test for response parsing (TypeScript / fast-check)
    - **Property 5: Response parsing validation**
    - Generate random strings (valid integers, invalid strings, negative numbers, floats, ERROR: prefixed), verify `handleWrappedLineCountResponse` sets correct verticalMax
    - `{ numRuns: 10 }`
    - **Validates: Requirements 3.4**

- [x] 6. Frontend: Remove `lineLengths` signal infrastructure
  - [x] 6.1 Remove signals, subscription, and methods from ShellStateService
    - Remove `lineLengths` signal
    - Remove `totalLogicalLines` signal
    - Remove `lineLengthsSubscription` field and its unsubscribe in `ngOnDestroy`
    - Remove `handleLineLengthsResponse` method
    - Remove `requestLineLengths` method
    - Remove `updateWrappedScrollbarMax` method
    - Remove `get-line-lengths` subscription from constructor
    - _Requirements: 5.4_

  - [x] 6.2 Update `verticalThumbFraction` computed signal for wrapped mode
    - Replace `lineLengths`-based visual row index computation with backend-resolved position tracking (use `startLine` and `characterOffset` from TabViewState with verticalMax from `get-wrapped-line-count`)
    - _Requirements: 5.2_

  - [x] 6.3 Update `activeTotalLogicalLines` computed signal
    - Remove dependency on `totalLogicalLines` signal; use scrollbar `verticalMax` directly (in wrapped mode this is visual row count from backend)
    - _Requirements: 5.4_

  - [x] 6.4 Remove `computeWrappedScrollbarMax` import and function from `line-wrap-utils.ts` if no longer used
    - _Requirements: 5.4_

- [x] 7. Frontend: Update scroll navigation for backend visual row resolution
  - [x] 7.1 Update wrapped-mode scroll requests to send visual row index
    - In scrollbar drag handler and wheel/arrow handlers for wrapped mode, compute visual row index and send it as the `startLine` field in the 6-field wrapped get-view request
    - Backend resolves to (startLine, characterOffset) via `ResolveVisualRowIndex`
    - _Requirements: 4.1, 5.2_

- [x] 8. Final checkpoint
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- Backend uses C# (.NET 10), frontend uses TypeScript (Angular 19)
- PBT iteration cap: FsCheck `[Property(MaxTest = 10)]`, fast-check `{ numRuns: 10 }`

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "2.1", "5.1", "5.2"] },
    { "id": 1, "tasks": ["1.2", "1.4", "1.5", "2.2", "2.3", "5.3", "5.4"] },
    { "id": 2, "tasks": ["1.3", "4.1", "4.2", "4.3", "6.1"] },
    { "id": 3, "tasks": ["6.2", "6.3", "6.4"] },
    { "id": 4, "tasks": ["7.1"] }
  ]
}
```
