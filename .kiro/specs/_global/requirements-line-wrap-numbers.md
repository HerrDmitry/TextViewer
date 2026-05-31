# Line Wrap & Line Numbers — Requirements

## Introduction

Line number gutter and wrap mode for the text viewer. Gutter displays 1-based line numbers to the left of content. Wrap mode hard-wraps lines at Col_Count boundary. Line numbers are provided by the backend in both modes (non-wrapped and wrapped responses include per-row line number metadata), eliminating frontend computation of gutter numbers.

Depends on: text-handling (view request/response, measurement), scroll-navigation (scrollbar, Start_Line/Start_Col), file-view-service (GetViewAsync, GetWrappedViewAsync).

## Glossary

- **Line_Number_Gutter**: Fixed-width column left of text content displaying 1-based line numbers per visible Logical_Line
- **Wrap_Mode**: Display mode where long lines hard-wrap at Col_Count character boundaries into multiple Visual_Rows
- **Total_Logical_Lines**: Total Logical_Lines in file for active tab's session; used for gutter width calculation
- **Visual_Row**: Single rendered row; in non-wrapped mode = one Logical_Line; in wrapped mode a Logical_Line may produce multiple Visual_Rows
- **Logical_Line**: File line delimited by newline characters; zero-based index
- **Wrap_Checkbox**: Checkbox on Status_Bar toggling Wrap_Mode
- **Character_Offset**: Zero-based position within a Logical_Line's content from which to begin retrieving characters in wrapped mode
- **Character_Count**: Content characters requested in wrapped mode; newline delimiters not counted but included in response
- **Gutter_Width**: Pixel width of Line_Number_Gutter = digits(Total_Logical_Lines) × Char_Metrics_width + 16px padding

## Requirements

### Requirement 1: Line Number Gutter Display

**User Story:** As a user, I want line numbers to the left of text content to identify which line I am viewing.

#### Acceptance Criteria

1. WHEN an active tab has view rows, THE Text_View_Area SHALL render a Line_Number_Gutter showing backend-provided 1-based line numbers for each visible row; rows with null line numbers (continuation rows in wrapped mode) SHALL show an empty gutter cell (non-breaking space) of same height as a Visual_Row
2. THE Line_Number_Gutter SHALL be fixed-position (does not scroll horizontally)
3. THE Line_Number_Gutter SHALL right-align numbers using same monospace font-family and font-size as text content
4. THE Gutter_Width SHALL be: digits(Total_Logical_Lines) × Char_Metrics_width + 16px (8px left + 8px right padding); minimum accommodates one digit plus padding; stable during scrolling regardless of visible lines or Wrap_Mode
5. WHEN viewport scrolls vertically, THE gutter SHALL update from the new backend response's line numbers
6. WHILE no active tab or no view rows loaded, THE Line_Number_Gutter SHALL not render
7. IF fewer rows returned than Row_Count, THEN gutter renders only as many cells as rows returned

### Requirement 2: Backend-Provided Line Numbers (Non-Wrapped)

**User Story:** As a user, I want each row to carry its line number from the backend so gutter numbers always match displayed content.

#### Acceptance Criteria

1. WHEN a get-view response arrives in non-wrapped mode, THE system SHALL display the line number provided by the backend for each row (format: `{lineNum}\t{rowContent}` per row), regardless of current frontend scroll position state
2. THE frontend SHALL parse each row by splitting on first `\t` → extract lineNum (parseInt) and row content; store line numbers in `gutterNumbers` on TabViewState
3. IF any row lacks a `\t` separator (malformed), THE frontend SHALL log error and keep previous state

### Requirement 3: Backend-Provided Line Numbers (Wrapped)

**User Story:** As a user, I want wrapped-mode line numbers from the backend so gutter numbers always match displayed content regardless of scroll timing.

#### Acceptance Criteria

1. WHEN a get-view response arrives in wrapped mode, THE system SHALL display backend-provided line numbers from the `L:` header (format: `L:{n1},{n2},...\n{content}`)
2. THE backend SHALL assign the 1-based line number to the first Visual_Row of each Logical_Line; continuation rows get null (empty in header)
3. WHEN a Logical_Line's first Visual_Row is scrolled above viewport but subsequent rows visible, THE backend SHALL assign the line number to the topmost visible Visual_Row
4. WHEN viewport resized, THE system SHALL request a new view and display line numbers from that fresh response

### Requirement 4: Wrap Mode Toggle

**User Story:** As a user, I want a checkbox to toggle line wrapping on and off.

#### Acceptance Criteria

1. THE Status_Bar SHALL render a Wrap_Checkbox labeled "Wrap" toggling Wrap_Mode
2. THE Wrap_Checkbox SHALL default to unchecked (Wrap_Mode off)
3. WHEN toggled on, THE frontend SHALL reset Start_Col to 0, send wrapped-mode view request (startLine, characterOffset=0, characterCount=colCount×rowCount, colCount)
4. WHEN toggled off, THE frontend SHALL send standard rectangular view request (startLine, startCol=0, rowCount, colCount)
5. Wrap_Mode is application-level (not per-tab); non-active tabs marked needsRefresh, get new request when activated
6. IF no active tab, THEN update state only, no request
7. IF error response to mode-switch request, THEN keep previous rows visible and display error

### Requirement 5: Wrapped Mode Content Request

**User Story:** As a developer, I want the frontend to request text by start line, character offset, character count, and col count in wrapped mode.

#### Acceptance Criteria

1. WHILE Wrap_Mode on, THE frontend SHALL send: View_Session_ID, startLine (zero-based), Character_Offset, Character_Count, and colCount
2. Character_Count = Col_Count × Row_Count (capped at 2,147,483,647)
3. Character_Offset = characters of current line scrolled past (multiples of Col_Count)
4. Payload format: `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount\ncolCount` (6 fields); backend also accepts 5-field legacy format (colCount defaults to 1)
5. On active tab change or dimension change, send new wrapped request (latest-wins cancellation)
6. On error response, keep previous rows visible and display error

### Requirement 6: Backend Wrapped Mode Response

**User Story:** As a developer, I want FileViewService to return character-count-based slices with per-visual-row line numbers.

#### Acceptance Criteria

1. WHEN wrapped request received (startLine ≥ 0, characterOffset ≥ 0, characterCount ≥ 1, colCount ≥ 1), THE backend SHALL extract up to characterCount content characters starting from specified position
2. Newline delimiters NOT counted toward characterCount but included in output
3. IF characterOffset exceeds start line content length, advance to subsequent lines
4. IF end of file reached before characterCount collected, return all remaining
5. Response format: `L:{n1},{n2},...\n{content}` — header with per-visual-row line numbers (integer or empty for continuation), then content
6. IF startLine beyond file, return empty (`L:\n`)
7. IF invalid params (startLine < 0, characterOffset < 0, characterCount < 1, colCount < 1), return `ERROR: {paramName} out of range`
8. IF scan in progress and startLine beyond scanned range, return empty

### Requirement 7: Wrapped Mode Rendering

**User Story:** As a user, I want wrapped text to break at viewport column boundary.

#### Acceptance Criteria

1. WHILE Wrap_Mode on, THE frontend SHALL split response content into Visual_Rows of at most Col_Count chars (hard wrap); newlines consumed as line-boundary markers
2. Each Visual_Row rendered as block-level element, same monospace font
3. Horizontal scrollbar hidden in wrapped mode
4. Vertical scrollbar Scrollbar_Max = sum of ceil(charLength / colCount) per Logical_Line (empty lines = 1)
5. Newline ends current Visual_Row (even if < Col_Count chars placed)
6. Empty response content → zero Visual_Rows

### Requirement 8: Wrapped Mode Vertical Scrolling

**User Story:** As a user, I want to scroll through wrapped content one Visual_Row at a time.

#### Acceptance Criteria

1. Vertical scroll actions advance by Visual_Rows using Character_Offset and startLine
2. Scroll down: offset += Col_Count; if offset ≥ line content length → next line, offset=0
3. Scroll up: offset -= Col_Count; if negative → previous line's last wrapped row offset
4. At top (line=0, offset=0) + scroll up → no change, no request
5. At last Visual_Row + scroll down → no change, no request
6. Wheel step = 3 Visual_Rows per tick (iterative application of single-row logic)
7. Arrow key step = 1 Visual_Row per press

### Requirement 9: Gutter Interaction with Viewport Measurement

**User Story:** As a developer, I want gutter width excluded from text content measurement.

#### Acceptance Criteria

1. Col_Count = floor((available_pixel_width − Gutter_Width) / Char_Metrics_width), minimum 1
2. WHEN Gutter_Width changes, recompute Col_Count and trigger new view request if changed
3. Row_Count unchanged by gutter presence
4. WHILE gutter not rendered, Col_Count uses full available width (Gutter_Width = 0)
