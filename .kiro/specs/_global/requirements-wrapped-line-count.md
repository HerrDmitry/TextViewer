# Wrapped Line Count — Requirements

## Introduction

Optimizes wrapped-mode scrollbar computation by replacing bulk `get-line-lengths` (one integer per line) with single-integer `get-wrapped-line-count` backend handler. Backend computes total visual rows server-side and resolves visual row indices for scroll navigation. Eliminates per-line data transfer across Photino bridge for large files.

Depends on: file-view-service (LineIndex, GetWrappedViewAsync), text-handling (scrollbar state, view requests), line-wrap-numbers (wrap mode, visual rows).

## Glossary

- **Wrapped_Line_Count_Handler**: Backend message handler computing total visual rows for a given column count
- **Visual_Row**: Single displayed row in wrapped mode; a logical line with char length L at column count C produces ceil(L/C) visual rows (minimum 1)
- **Visual_Row_Index**: Zero-based index into flattened sequence of all visual rows; used by frontend for scroll position and by backend for position resolution

## Requirements

### Requirement 1: Backend Wrapped Line Count Handler

**User Story:** As a frontend consumer, I want a backend handler that returns the total visual row count for a given column width, so that the scrollbar can be sized without transferring per-line data.

#### Acceptance Criteria

1. WHEN the frontend sends a `get-wrapped-line-count` message with payload `{sessionId}\n{colCount}`, THE handler SHALL return a single integer representing total visual row count
2. THE handler SHALL compute each line's visual rows as ceil(charLen/colCount) where charLen is character length
3. WHEN a line has zero character length, THE handler SHALL count it as 1 visual row
4. WHEN character length data is not yet available for a line, THE handler SHALL fall back to byte length from LineIndex
5. IF the session ID is not found, THEN THE handler SHALL return an error string prefixed with `ERROR:`
6. IF the column count is less than 1, THEN THE handler SHALL return an error string prefixed with `ERROR:`

### Requirement 2: Concurrent Computation

**User Story:** As a system operator, I want the computation to leverage parallel execution, so that response time remains acceptable for large files.

#### Acceptance Criteria

1. THE handler SHALL iterate LineIndex lines concurrently using Parallel.For with thread-local accumulators
2. THE handler SHALL produce the same result regardless of iteration order (deterministic sum)

### Requirement 3: Frontend Scrollbar Integration

**User Story:** As a user in wrap mode, I want the scrollbar to reflect correct total visual rows without waiting for per-line data transfer.

#### Acceptance Criteria

1. WHEN wrap mode is toggled on and scan is complete, THE frontend SHALL request `get-wrapped-line-count`
2. WHEN a scan completes while wrap mode is active, THE frontend SHALL request `get-wrapped-line-count`
3. WHEN the viewport is resized while wrap mode is active, THE frontend SHALL request `get-wrapped-line-count` (existing 150ms debounce)
4. WHEN the response is received, THE frontend SHALL validate it is a non-negative integer and set verticalMax; IF invalid, use 0 as fallback

### Requirement 4: Backend Visual Row Index Resolution

**User Story:** As a frontend consumer, I want to send a visual row index and receive wrapped content starting at that position.

#### Acceptance Criteria

1. WHEN the frontend sends a wrapped get-view request with a visual row index, THE backend SHALL resolve it to (startLine, characterOffset) and return content from that position
2. THE backend SHALL iterate lines summing ceil(charLen/colCount) until cumulative sum reaches target
3. WHEN the visual row index exceeds total visual rows, THE backend SHALL clamp to last visual row
4. WHEN the visual row index is 0, THE backend SHALL return content from line 0, offset 0

### Requirement 5: Elimination of get-line-lengths

**User Story:** As a system operator, I want the frontend to never request bulk per-line length data.

#### Acceptance Criteria

1. THE frontend SHALL NOT send `get-line-lengths` requests — replaced by `get-wrapped-line-count`
2. THE `get-line-lengths` backend handler SHALL be removed entirely
3. THE frontend `lineLengths` signal, `totalLogicalLines` signal, `requestLineLengths`, `handleLineLengthsResponse`, and `lineLengthsSubscription` SHALL be removed

### Requirement 6: Backend Caching

**User Story:** As a system operator, I want the backend to cache computed results per session.

#### Acceptance Criteria

1. THE handler SHALL cache total visual row count per session, keyed by (sessionId, colCount, lineCount)
2. WHEN colCount AND lineCount unchanged, THE handler SHALL return cached value without recomputation
3. WHEN lineCount changed (scan progressed), THE handler SHALL recompute and update cache
4. WHEN colCount changed, THE handler SHALL recompute and update cache
5. WHEN a session is closed, THE cache entry SHALL be removed
