# Short Line Horizontal Scroll Bugfix Design

## Overview

When horizontally scrolled past a short line's content length, the backend correctly returns an empty string for that row. The frontend renders it as `<div class="view-row"></div>` — but with `white-space: pre` and no content, the div produces no line box and collapses to zero pixel height. The fix adds a CSS `min-height` to `.view-row` so empty rows maintain consistent vertical spacing.

## Glossary

- **Bug_Condition (C)**: A view row whose content is empty string (line shorter than startCol offset)
- **Property (P)**: All rendered rows occupy exactly one line-height of vertical space regardless of content
- **Preservation**: Rows with visible content continue to render identically (same font metrics, no extra spacing)
- **startCol**: Zero-based horizontal scroll offset (first visible column)
- **viewRows**: Array of strings from backend get-view response, one per visible line in viewport
- **StripDelimiter**: Backend function that removes trailing `\r\n`/`\n` from row content before sending

## Bug Details

### Bug Condition

The bug manifests when the backend returns an empty string for a row because the line's content length is less than startCol. The CSS `.view-row` class uses `white-space: pre` without a `min-height`, causing empty divs to collapse.

**Formal Specification:**
```
FUNCTION isBugCondition(input)
  INPUT: input of type { lineContent: string, startCol: number, colCount: number }
  OUTPUT: boolean
  
  RETURN startCol >= lineContent.length
END FUNCTION
```

**Data Flow:**
1. Backend `FileViewService.GetViewAsync`: when `startCol >= content.Length` → `rows.Add(delimiter)`
2. Backend `HandleGetView`: `StripDelimiter(row)` strips the delimiter → empty string `""`
3. Response format: `"{lineNum}\t"` (lineNum + tab + empty content)
4. Frontend `handleViewResponse`: parses → `parsedRows.push("")`
5. Template: `<div class="view-row">{{ row }}</div>` → `<div class="view-row"></div>`
6. CSS: `white-space: pre` + empty content = no line box = zero height

### Examples

- Line "Hello" (5 chars), startCol=10: backend returns `""`, row collapses to 0px height
- Line "" (0 chars), startCol=3: backend returns `""`, row collapses to 0px height
- Line "Short" (5 chars), startCol=5: backend returns `""`, row collapses to 0px height
- Line "Long enough text" (16 chars), startCol=5: backend returns `"enough text"` → renders normally

## Expected Behavior

### Preservation Requirements

**Unchanged Behaviors:**
- Rows with non-empty content render with the same font metrics and height as before
- Horizontal scrollbar behavior and max values remain unchanged
- Backend response format and content remain unchanged (empty string is correct)
- Gutter line numbers continue to align with their corresponding rows
- Wrap mode rendering is unaffected (no startCol in wrap mode)

**Scope:**
All rows where content is non-empty (`startCol < lineContent.length`) are completely unaffected by this fix. The fix only ensures zero-content rows maintain the same height as content rows.

## Hypothesized Root Cause

Based on code analysis, the confirmed root cause:

1. **CSS Missing min-height**: `.view-row` in `text-view-area.component.css` has `white-space: pre` but no `min-height`. When element content is empty string, `white-space: pre` does not generate a line box (unlike `white-space: normal` which would generate an anonymous inline box). Result: element computes to 0px height.

2. **Backend behavior is correct**: `FileViewService.GetViewAsync` returns the delimiter for short lines, then `StripDelimiter` removes it. Empty string correctly communicates "this line has no visible content at this horizontal offset." No backend change needed.

3. **Frontend parsing is correct**: `handleViewResponse` correctly parses `"{lineNum}\t"` as row content `""`. No parsing change needed.

## Correctness Properties

Property 1: Bug Condition - Empty Rows Maintain Height

_For any_ rendered view row whose content is empty string (because line length < startCol), the row element SHALL occupy the same vertical height as a row containing visible content (one line-height unit, matching the monospace font metrics).

**Validates: Requirements 2.1, 2.2**

Property 2: Preservation - Non-Empty Rows Unchanged

_For any_ rendered view row whose content is non-empty (line length >= startCol), the row element SHALL render with exactly the same dimensions, font, and appearance as before the fix, preserving all existing visual behavior.

**Validates: Requirements 3.1, 3.2, 3.3, 3.4**

## Fix Implementation

### Changes Required

**File**: `ClientApp/src/app/shell/text-view-area/text-view-area.component.css`

**Selector**: `.view-row`

**Specific Changes**:
1. **Add `min-height: 1lh`**: The `lh` unit equals the element's computed `line-height`, ensuring every row (including empty ones) occupies exactly one line of vertical space. This is the most semantically correct fix — it directly ties height to the font metrics already in use.

   Fallback consideration: `1lh` is supported in all modern browsers (Chrome 109+, Firefox 120+, Safari 16.4+). Since this app uses Photino with WebView2 (Chromium-based) on Windows, WebKit on macOS/Linux — all support `lh` unit.

2. **No backend changes**: The backend correctly returns empty string for short lines. Changing this would add unnecessary complexity.

3. **No template changes**: No `&nbsp;` placeholder needed — CSS fix is cleaner and doesn't pollute the data model.

## Testing Strategy

### Validation Approach

The testing strategy follows a two-phase approach: first, surface counterexamples that demonstrate the bug on unfixed code, then verify the fix works correctly and preserves existing behavior.

### Exploratory Bug Condition Checking

**Goal**: Surface counterexamples that demonstrate the bug BEFORE implementing the fix. Confirm that the shell-state service delivers empty-string rows to the template when startCol exceeds line length.

**Test Plan**: Write property-based tests in the frontend that construct `viewRows` arrays containing empty strings (simulating the backend response for short lines scrolled past) and verify the rendering behavior. On unfixed code, assert that the expected behavior (consistent row height) is violated.

**Test Cases**:
1. **Single short line**: viewRows with one empty string among non-empty rows — verify row count in DOM matches array length (will fail on unfixed code if DOM collapses empty rows)
2. **Multiple consecutive short lines**: viewRows with several empty strings — verify total rendered height equals expected (will fail on unfixed code)
3. **All lines empty**: viewRows all empty strings — verify container still has expected height (will fail on unfixed code)

**Expected Counterexamples**:
- Empty-string rows render to 0px height, causing total content height < expected
- Line count in DOM matches but visual height is incorrect

### Fix Checking

**Goal**: Verify that for all inputs where the bug condition holds, the fixed function produces the expected behavior.

**Pseudocode:**
```
FOR ALL viewRows WHERE any row is "" (empty string) DO
  renderedHeight := measureRowHeight(emptyRow)
  expectedHeight := measureRowHeight(nonEmptyRow)
  ASSERT renderedHeight = expectedHeight
END FOR
```

### Preservation Checking

**Goal**: Verify that for all inputs where the bug condition does NOT hold, the fixed function produces the same result as the original function.

**Pseudocode:**
```
FOR ALL viewRows WHERE all rows are non-empty DO
  ASSERT renderOutput_fixed(viewRows) = renderOutput_original(viewRows)
END FOR
```

**Testing Approach**: Property-based testing verifies that non-empty rows maintain identical height before and after the CSS fix. Since this is a CSS-only change, preservation testing focuses on ensuring no unintended spacing changes for content rows.

**Test Cases**:
1. **Non-empty row height preservation**: Verify rows with content maintain same height after fix
2. **Mixed content preservation**: Verify files where all lines are longer than startCol render identically
3. **Zero startCol preservation**: Verify no horizontal scroll state is unaffected

### Unit Tests

- Test that shell-state service correctly stores empty-string rows from backend response
- Test that viewRows signal contains empty strings when backend returns them for short lines

### Property-Based Tests

- Generate random viewRows arrays with mix of empty and non-empty strings; verify all rows render to same height
- Generate random viewRows arrays with all non-empty strings; verify height unchanged from baseline

### Integration Tests

- Open a file with mixed line lengths, scroll horizontally past short lines, verify visual alignment
- Verify gutter numbers remain aligned with corresponding content rows after horizontal scroll
