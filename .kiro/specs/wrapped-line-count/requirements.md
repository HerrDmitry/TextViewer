#[[file:.kiro/specs/_global/requirements-shared.md]]

# Requirements Document

## Introduction

Optimize the wrapped-mode scrollbar computation by replacing the bulk `get-line-lengths` payload (one integer per line, newline-delimited) with a single-integer `get-wrapped-line-count` backend handler and backend visual row index resolution. For large files (100K+ lines), the current approach floods the Photino WebView bridge. The new handler computes the total visual row count server-side and returns a single integer. Scroll navigation uses backend-resolved visual row indices. `get-line-lengths` is removed entirely.

## Glossary

- **Wrapped_Line_Count_Handler**: Backend message handler registered on MessageBusHost that computes total visual rows for a given column count
- **Visual_Row**: A single displayed row in wrapped mode; a logical line with char length `L` at column count `C` produces `ceil(L / C)` visual rows (minimum 1)
- **Visual_Row_Index**: Zero-based index into the flattened sequence of all visual rows across all logical lines; used by frontend for scroll position and by backend for position resolution

## Requirements

### Requirement 1: Backend Wrapped Line Count Handler

**User Story:** As a frontend consumer, I want a backend handler that returns the total visual row count for a given column width, so that the scrollbar can be sized without transferring per-line data.

#### Acceptance Criteria

1. WHEN the frontend sends a `get-wrapped-line-count` message with payload `{sessionId}\n{colCount}`, THE Wrapped_Line_Count_Handler SHALL return a single integer representing the total visual row count
2. THE Wrapped_Line_Count_Handler SHALL compute each line's visual rows as `ceil(charLen / colCount)` where `charLen` is the character length of that line
3. WHEN a line has zero character length, THE Wrapped_Line_Count_Handler SHALL count it as 1 visual row
4. WHEN character length data is not yet available for a line, THE Wrapped_Line_Count_Handler SHALL fall back to byte length from LineIndex
5. IF the session ID is not found, THEN THE Wrapped_Line_Count_Handler SHALL validate session existence first before any other processing and return an error string prefixed with `ERROR:`
6. IF the column count is less than 1, THEN THE Wrapped_Line_Count_Handler SHALL return an error string prefixed with `ERROR:`

### Requirement 2: Concurrent Segment Iteration

**User Story:** As a system operator, I want the wrapped line count computation to leverage concurrent segment iteration, so that response time remains acceptable for large files.

#### Acceptance Criteria

1. THE Wrapped_Line_Count_Handler SHALL iterate LineIndex segments concurrently using parallel execution (Parallel.For or equivalent)
2. WHEN computing the total visual row count, THE Wrapped_Line_Count_Handler SHALL produce the same result regardless of iteration order (deterministic sum)

### Requirement 3: Frontend Scrollbar Trigger Replacement

**User Story:** As a user in wrap mode, I want the scrollbar to reflect the correct total visual rows without waiting for per-line data transfer, so that the UI responds quickly after toggling wrap mode or completing a scan.

#### Acceptance Criteria

1. WHEN wrap mode is toggled on and scan is complete, THE Shell_State_Service SHALL request `get-wrapped-line-count` instead of `get-line-lengths` for scrollbar computation
2. WHEN a scan completes while wrap mode is active, THE Shell_State_Service SHALL request `get-wrapped-line-count` for scrollbar computation
3. WHEN the viewport is resized while wrap mode is active, THE Shell_State_Service SHALL request `get-wrapped-line-count` using the existing 150ms resize debounce
4. WHEN the `get-wrapped-line-count` response is received, THE Shell_State_Service SHALL validate the response is a non-negative integer and set `verticalMax` on the active tab's scrollbar state to the returned value; IF the response is invalid, THE Shell_State_Service SHALL use 0 as fallback

### Requirement 4: Backend Visual Row Index Resolution

**User Story:** As a frontend consumer, I want to send a visual row index to the backend and receive wrapped content starting at that position, so that scroll navigation works without per-line length data on the frontend.

#### Acceptance Criteria

1. WHEN the frontend sends a wrapped get-view request with a visual row index, THE backend SHALL resolve the visual row index to (startLine, characterOffset) and return content from that position
2. THE backend SHALL compute the mapping: iterate lines summing ceil(charLen/colCount) until cumulative sum reaches the target visual row index, yielding the logical line and character offset within that line
3. WHEN the visual row index exceeds total visual rows, THE backend SHALL clamp to the last visual row
4. WHEN the visual row index is 0, THE backend SHALL return content from line 0, offset 0

### Requirement 5: Elimination of get-line-lengths for Scrollbar

**User Story:** As a system operator, I want the frontend to never request bulk per-line length data, so that the communication channel is not flooded.

#### Acceptance Criteria

1. THE frontend SHALL NOT send `get-line-lengths` requests for scrollbar computation — replaced by `get-wrapped-line-count` which SHALL be used when scrollbar computation is needed in wrap mode
2. THE frontend SHALL NOT send `get-line-lengths` requests for scroll navigation — replaced by backend visual row index resolution
3. THE `get-line-lengths` backend handler SHALL be removed entirely (handler registration, `HandleGetLineLengths` method, and frontend subscription/signal infrastructure)
4. THE frontend `lineLengths` signal, `totalLogicalLines` signal, `handleLineLengthsResponse`, `requestLineLengths`, and `lineLengthsSubscription` SHALL be removed

### Requirement 6: Backend Caching

**User Story:** As a system operator, I want the backend to cache the computed wrapped line count per session, so that repeated requests with the same column width and unchanged scan state return instantly without recomputation.

#### Acceptance Criteria

1. THE Wrapped_Line_Count_Handler SHALL cache the computed total visual row count per session, keyed by (sessionId, colCount, lineCount)
2. WHEN a request arrives with the same colCount AND LineIndex.LineCount has not changed since last computation, THE handler SHALL return the cached value without recomputation; WHEN cache conditions are not satisfied (colCount or LineCount changed), THE handler SHALL NOT return the cached value and SHALL recompute
3. WHEN LineIndex.LineCount has changed since last computation (scan progressed), THE handler SHALL recompute and update the cache
4. WHEN colCount differs from the cached colCount, THE handler SHALL recompute and update the cache
5. WHEN a session is closed (close-file), THE cache entry for that session SHALL be removed
