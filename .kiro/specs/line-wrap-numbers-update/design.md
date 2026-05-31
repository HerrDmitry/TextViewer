#[[file:.kiro/specs/_global/design-shared.md]]

# Line Wrap Numbers Update — Bugfix Design

## Overview

Line numbers displayed in the gutter drift out of sync with displayed content. In non-wrapped mode, the frontend computes line numbers from `startLine` state which can be stale relative to the most recent get-view response. In wrapped mode, the frontend re-parses the raw response content to detect line boundaries, which fails under various edge cases (partial-line offsets, resize races, inconsistent state).

The fix moves line number computation to the backend. Both `GetViewAsync` (non-wrapped) and `GetWrappedViewAsync` (wrapped) responses will include per-row line number metadata. The frontend displays these numbers directly, eliminating all local computation of gutter numbers.

## Glossary

- **Bug_Condition (C)**: The condition that triggers the bug — frontend-computed line numbers are used instead of backend-provided ones
- **Property (P)**: The desired behavior — gutter displays backend-provided line numbers that always match the displayed content
- **Preservation**: Existing gutter width computation, request formats, error handling, scrollbar behavior, and all non-gutter rendering that must remain unchanged
- **GetViewAsync**: The method in `Services/FileViewService.cs` that extracts rectangular view regions (non-wrapped mode)
- **GetWrappedViewAsync**: The method in `Services/FileViewService.cs` that extracts character-count-based slices (wrapped mode)
- **computeNonWrappedLineNumbers**: The function in `ClientApp/src/app/shell/line-wrap-utils.ts` that computes line numbers from `startLine` (to be removed)
- **computeWrappedGutterNumbers**: The function in `ClientApp/src/app/shell/line-wrap-utils.ts` that parses response content to derive gutter numbers (to be removed)
- **activeGutterNumbers**: The computed signal in `ShellStateService` that produces the gutter number array for the active tab

## Bug Details

### Bug Condition

The bug manifests whenever the frontend computes gutter line numbers locally from scroll position state (`startLine`, `characterOffset`) or by parsing response content, rather than receiving authoritative per-row line numbers from the backend. Race conditions between async view responses and scroll position updates cause drift in non-wrapped mode, and content-parsing in wrapped mode fails when state is inconsistent.

**Formal Specification:**
```
FUNCTION isBugCondition(input)
  INPUT: input of type ViewResponse
  OUTPUT: boolean

  RETURN input.response does NOT include per-row line number annotations
         AND frontend computes gutter numbers from local state
END FUNCTION
```

### Examples

- User scrolls down rapidly in non-wrapped mode; a get-view response arrives after `startLine` has already been updated by a subsequent scroll → gutter shows `startLine + 1` which no longer matches the rows in the response (off by WHEEL_STEP lines)
- User is in wrapped mode viewing a line that wraps across 5 visual rows; `characterOffset` is updated by scroll but `rawResponseContent` still holds the previous response → `computeWrappedGutterNumbers` assigns line numbers to wrong rows
- User resizes the viewport in wrapped mode; `colCount` changes but `rawResponseContent` is stale → `splitIntoVisualRows` produces different row boundaries than the actual displayed content, misaligning gutter numbers
- User scrolls within a long wrapped line where `characterOffset > 0`; the topmost-visible-row rule assigns the line number correctly only if `characterOffset` and `rawResponseContent` are perfectly in sync — any timing mismatch produces wrong numbers

## Expected Behavior

### Preservation Requirements

**Unchanged Behaviors:**
- Gutter_Width computation from Total_Logical_Lines digit count × Char_Metrics width + 16px padding
- Non-wrapped get-view request format: `viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount`
- Wrapped-mode get-view request format: `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount`
- Error response handling (display error message, keep previous rows visible)
- Empty-state rendering (no gutter when no active tab or no view rows)
- Wrap mode toggle behavior (reset Start_Col, mark non-active tabs needsRefresh, send appropriate request)
- Scrollbar polling and verticalMax/horizontalMax computation
- `splitIntoVisualRows` function behavior (still used for row rendering in wrapped mode)
- `computeGutterWidth` function behavior
- `computeWrappedScrollbarMax` function behavior
- All scroll navigation logic (wheel, arrow keys, thumb drag)

**Scope:**
All inputs that do NOT involve gutter number computation should be completely unaffected by this fix. This includes:
- View request sending and response parsing (row content extraction)
- Scrollbar state management
- Tab lifecycle (open, close, activate)
- Measurement pipeline (ResizeObserver, char metrics)

## Hypothesized Root Cause

Based on the bug description, the most likely issues are:

1. **Temporal Mismatch (Non-Wrapped)**: `computeNonWrappedLineNumbers(state.startLine, rows.length)` uses the current `startLine` signal value at render time, but the `viewRows` in the response were fetched for a *previous* `startLine`. When the user scrolls rapidly, `startLine` advances before the response arrives, so the computed numbers are offset from the actual content.

2. **State Inconsistency (Wrapped)**: `computeWrappedGutterNumbers` reads `rawResponseContent`, `colCount`, `startLine`, and `characterOffset` — four independent pieces of state that can be out of sync. The response content corresponds to one (startLine, characterOffset) pair, but by the time the computed signal evaluates, the position signals may have already been updated by a new scroll action.

3. **Content Parsing Fragility (Wrapped)**: The algorithm walks the raw content character-by-character to detect newline boundaries and col-count wraps. Any mismatch between the content's actual structure and the assumed (startLine, characterOffset) produces cascading errors in line number assignment for all subsequent rows.

4. **Resize Race (Wrapped)**: On resize, `colCount` changes immediately (from measurement), but `rawResponseContent` still holds content fetched with the old `colCount`. The gutter computation uses the new `colCount` to split the old content, producing different row boundaries than what the backend would produce.

## Correctness Properties

Property 1: Bug Condition - Backend-Provided Line Numbers Match Displayed Rows

_For any_ get-view response (wrapped or non-wrapped) where the backend includes per-row line number metadata, the frontend SHALL display exactly those line numbers in the gutter, with each number aligned to its corresponding row, regardless of the current frontend scroll position state.

**Validates: Requirements 2.1, 2.2, 2.3, 2.4**

Property 2: Preservation - Non-Gutter Behavior Unchanged

_For any_ input that does NOT involve gutter number rendering (request formats, scrollbar computation, error handling, row content display, tab lifecycle), the fixed code SHALL produce exactly the same behavior as the original code, preserving all existing functionality.

**Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7**

## Protocol Contract

### Non-Wrapped Response Format (Breaking Change)

**Old format** (rows joined by `\n`):
```
row0content\nrow1content\nrow2content
```

**New format** (each row prefixed w/ 1-based line number + TAB separator):
```
{lineNum}\t{rowContent}\n{lineNum}\t{rowContent}\n...
```

**Delimiter rules:**
- Split on FIRST `\t` only per row — remainder is row content verbatim (content MAY contain tabs)
- Frontend parsing: `const tabIdx = row.indexOf('\t'); const lineNum = parseInt(row.substring(0, tabIdx)); const content = row.substring(tabIdx + 1);`
- If `tabIdx === -1` → malformed response, treat as error

### Wrapped Response Format (Breaking Change)

**Old format** (pure content string):
```
{content}
```

**New format** (line-numbers header + newline + content):
```
L:{n1},{n2},{n3},...\n{content}
```

**Header rules:**
- First line starts w/ `L:` prefix → line-numbers header
- Comma-separated values: integer = line number for that visual row, empty = continuation row (null)
- Frontend parsing: `const headerEnd = response.indexOf('\n'); const header = response.substring(2, headerEnd); const content = response.substring(headerEnd + 1);`
- Parse: `header.split(',').map(v => v === '' ? null : parseInt(v))`

### Alignment Invariant (CRITICAL)

**Non-wrapped**: `LineNumbers.Length == Rows.Length` (backend guarantees, trivially true since both derived from same row extraction loop)

**Wrapped**: `LineNumbers.Length == splitIntoVisualRows(content, colCount).Length` — backend MUST produce exactly one line-number entry per visual row the frontend will render. Backend uses same col-count-based splitting logic as frontend `splitIntoVisualRows` to determine row boundaries.

**Violation → bug**: If lengths mismatch, frontend displays misaligned gutter numbers. Backend MUST assert this invariant before serializing response.

### Test Impact

**`TextViewer.Tests\BackendHandlerTests.cs`**: All `HandleGetView` assertions expect old payload format → MUST be updated to expect new `{lineNum}\t{content}` format (non-wrapped) and `L:` header (wrapped). This is non-trivial — every existing response assertion changes.

**Frontend test files** (`shell-state.service.spec.ts`, `shell-state.text-handling.spec.ts`, property specs): All mock responses must include new format. Existing mocks that return plain row strings → update to include tab-prefixed line numbers.

## Fix Implementation

### Changes Required

Assuming our root cause analysis is correct:

**File**: `Services/FileViewService.cs`

**Method**: `GetViewAsync`

**Change**: Return line number metadata alongside row content. Add a `LineNumbers` property to `ViewResult` containing the 1-based line number for each returned row.

1. **Extend ViewResult**: Add `IReadOnlyList<int> LineNumbers` property — parallel array to `Rows`, containing `startLine + i + 1` for each row `i`.

**File**: `Services/FileViewService.cs`

**Method**: `GetWrappedViewAsync`

**Change**: Return per-visual-row line number metadata. Track which logical line each character belongs to during extraction. For each visual row (Col_Count characters or newline-terminated), emit the 1-based logical line number on the first visual row of that line, and `null` for continuation rows.

2. **New return type for wrapped mode**: Change from `Result<string, ViewError>` to `Result<WrappedViewResult, ViewError>` where `WrappedViewResult` contains `Content` (string) and `LineNumbers` (list of nullable ints, one per visual row the frontend will produce).

**Invariant assertion**: Before returning, assert `LineNumbers.Count == splitIntoVisualRows(Content, colCount).Count` equivalent logic. If violated → internal error (indicates splitting logic divergence).

**File**: `Program.cs`

**Function**: `HandleGetView` (standard mode)

**Change**: Include line numbers in the response payload.

3. **Response format change (non-wrapped)**: Prefix each row with its 1-based line number and a tab separator: `{lineNum}\t{rowContent}`. Frontend splits on first `\t` only — content after first tab is verbatim row content (may contain additional tabs).

**File**: `Program.cs`

**Function**: `HandleGetView` (wrapped mode)

**Change**: Include line numbers as a header before the content.

4. **Response format change (wrapped)**: Prepend a line-numbers header to the response: `L:{n1},{n2},{n3},...\n{content}`. Each `n` is either a 1-based line number or empty string (for continuation rows). The frontend parses this header before processing content.

**File**: `ClientApp/src/app/shell/shell-state.service.ts`

**Function**: `handleViewResponse`

**Change**: Parse line number metadata from response payload.

5. **Non-wrapped parsing**: Split each row on first `\t` (indexOf, not split) → extract line number (parseInt) and row content. Store line numbers in a new `gutterNumbers` field on `TabViewState`. If no `\t` found → treat as malformed, log error, keep previous state.

6. **Wrapped parsing**: Extract `L:...` header line (first `\n`), parse comma-separated values into `(number | null)[]`. Store in `gutterNumbers` field on `TabViewState`. Pass remaining content (after header newline) to `splitIntoVisualRows` as before.

**File**: `ClientApp/src/app/shell/shell.types.ts`

**Interface**: `TabViewState`

**Change**: Add `gutterNumbers: (number | null)[] | null` field.

7. **New field**: `gutterNumbers` stores backend-provided line numbers per visual row, parallel to `viewRows`.

**File**: `ClientApp/src/app/shell/shell-state.service.ts`

**Signal**: `activeGutterNumbers`

**Change**: Replace computation logic with direct read from `TabViewState.gutterNumbers`.

8. **Simplify computed signal**: `activeGutterNumbers` returns `state.gutterNumbers ?? []` instead of calling `computeNonWrappedLineNumbers` or `computeWrappedGutterNumbers`.

**File**: `ClientApp/src/app/shell/line-wrap-utils.ts`

**Functions**: `computeNonWrappedLineNumbers`, `computeWrappedGutterNumbers`

**Change**: Mark as deprecated or remove. No longer called from production code.

9. **Remove dead code**: Delete `computeNonWrappedLineNumbers` and `computeWrappedGutterNumbers` (and their tests if they exist as unit tests; property tests may be repurposed).

**File**: `ClientApp/src/app/shell/shell.types.ts`

**Interface**: `TabViewState`

**Change**: `rawResponseContent` field can be removed since it was only needed for `computeWrappedGutterNumbers`.

10. **Remove rawResponseContent**: Delete the field from `TabViewState` and all code that populates it.

## Testing Strategy

### Validation Approach

The testing strategy follows a two-phase approach: first, surface counterexamples that demonstrate the bug on unfixed code, then verify the fix works correctly and preserves existing behavior.

### Exploratory Bug Condition Checking

**Goal**: Surface counterexamples that demonstrate the bug BEFORE implementing the fix. Confirm or refute the root cause analysis. If we refute, we will need to re-hypothesize.

**Test Plan**: Write tests that simulate rapid scrolling scenarios where `startLine` changes between request send and response arrival. Run these tests on the UNFIXED code to observe that `activeGutterNumbers` produces values mismatched with the actual response content.

**Test Cases**:
1. **Non-Wrapped Stale startLine**: Simulate a get-view response arriving after startLine has been incremented by a scroll action → gutter numbers will be offset (will fail on unfixed code)
2. **Wrapped Mode State Inconsistency**: Set `rawResponseContent` to content fetched at characterOffset=0, then update characterOffset to colCount before the computed signal evaluates → gutter numbers will be wrong (will fail on unfixed code)
3. **Wrapped Mode Resize Race**: Change colCount after caching rawResponseContent → `computeWrappedGutterNumbers` splits content differently than the actual displayed rows (will fail on unfixed code)
4. **Wrapped Mode Long Line Partial Scroll**: Set characterOffset to a mid-line position with content that doesn't align → incorrect line boundary detection (will fail on unfixed code)

**Expected Counterexamples**:
- `activeGutterNumbers` returns `[6, 7, 8, ...]` when displayed rows actually correspond to lines `[4, 5, 6, ...]`
- Possible causes: temporal mismatch between startLine signal and response content, stale rawResponseContent

### Fix Checking

**Goal**: Verify that for all inputs where the bug condition holds (backend provides per-row line numbers), the frontend displays exactly those numbers.

**Pseudocode:**
```
FOR ALL input WHERE isBugCondition(input) DO
  response := HandleGetView_fixed(input.request)
  parsedNumbers := parseLineNumbers(response)
  displayedNumbers := activeGutterNumbers after processing response
  ASSERT parsedNumbers = displayedNumbers
  ASSERT each number aligns with its corresponding row
END FOR
```

### Preservation Checking

**Goal**: Verify that for all inputs where the bug condition does NOT hold (non-gutter behavior), the fixed function produces the same result as the original function.

**Pseudocode:**
```
FOR ALL input WHERE NOT isBugCondition(input) DO
  ASSERT originalBehavior(input) = fixedBehavior(input)
END FOR
```

**Testing Approach**: Property-based testing is recommended for preservation checking because:
- It generates many request payloads automatically across the input domain
- It catches edge cases in response parsing that manual unit tests might miss
- It provides strong guarantees that non-gutter behavior is unchanged

**Test Plan**: Observe behavior on UNFIXED code first for request format generation, scrollbar computation, error handling, and tab lifecycle, then write property-based tests capturing that behavior.

**Test Cases**:
1. **Request Format Preservation**: Verify that non-wrapped requests still use `viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount` format and wrapped requests still use `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount` format
2. **Gutter Width Preservation**: Verify `computeGutterWidth` continues to produce the same results for all inputs (function unchanged)
3. **Scrollbar Preservation**: Verify scrollbar verticalMax/horizontalMax computation is unchanged
4. **Error Handling Preservation**: Verify ERROR: responses still display error message and keep previous rows visible

### Unit Tests

- Test backend `GetViewAsync` returns correct line numbers for each row (startLine + i + 1)
- Test backend `GetWrappedViewAsync` returns correct line numbers (first visual row of each logical line gets number, continuations get null)
- Test frontend response parsing extracts line numbers correctly from tab-separated format (non-wrapped)
- Test frontend response parsing extracts line numbers correctly from `L:` header format (wrapped)
- Test `activeGutterNumbers` returns backend-provided numbers directly without recomputation

### Property-Based Tests

- Generate random (startLine, rowCount) pairs and verify backend always returns exactly rowCount line numbers starting at startLine + 1
- Generate random wrapped content with varying line lengths and colCount, verify line number count matches visual row count from `splitIntoVisualRows`
- Generate random scroll sequences and verify gutter numbers always match the response they came from (no drift)

### Integration Tests

- Test full flow: open file → scroll → verify gutter numbers match displayed content at every step
- Test wrap mode toggle → verify gutter numbers update correctly from fresh backend response
- Test resize → verify gutter numbers come from new response (not recomputed from stale data)
