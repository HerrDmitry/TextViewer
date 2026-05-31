# Requirements Document

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

Line Wrap & Line Numbers feature. Adds two capabilities to the text viewer: (1) a line number gutter displayed to the left of the text content, and (2) a wrap mode (hard wrap at column boundary) toggled via a status bar checkbox. In non-wrapped mode, line numbers align one-to-one with displayed rows. In wrapped mode, a single logical line may span multiple visual rows; the line number remains visible as long as any row of that line is on screen. The line number gutter stays fixed when text is scrolled horizontally. When wrap mode is active, the frontend requests text from the backend using a character-offset-based slice (start line, character offset within that line, and character count) instead of the column-based rectangular view, and the backend returns the requested number of content characters (not counting newline delimiters toward the count, but including them in the output when encountered).

Depends on: text-handling (view request/response, measurement), scroll-navigation (scrollbar interaction, Start_Line/Start_Col), file-view-service (GetViewAsync).

## Glossary

- **Line_Number_Gutter**: A fixed-width column rendered to the left of the text content area displaying the 1-based line number for each visible logical line
- **Wrap_Mode**: A display mode where long lines are hard-wrapped (broken at exact Col_Count character boundaries, not at word boundaries) into multiple visual rows rather than extending beyond the visible area
- **Total_Logical_Lines**: The total number of Logical_Lines in the file for the active tab's session; used for gutter width calculation and distinct from Scrollbar_Max which in wrapped mode represents total Visual_Rows
- **Visual_Row**: A single rendered row in the text view area; in non-wrapped mode each Visual_Row corresponds to one logical line; in wrapped mode a logical line may produce multiple Visual_Rows
- **Logical_Line**: A line in the source file delimited by newline characters; identified by its zero-based index in the file
- **Wrap_Checkbox**: A checkbox control on the Status_Bar that toggles Wrap_Mode on or off
- **Character_Offset**: Zero-based position within a logical line's content (excluding delimiter) from which to begin retrieving characters in wrapped mode
- **Character_Count**: Number of content characters requested from the backend in wrapped mode; newline delimiters are not counted toward this total but are included in the response when encountered
- **Gutter_Width**: The pixel width of the Line_Number_Gutter, determined by the number of digits needed to display the largest visible line number multiplied by Char_Metrics width, plus padding

## Requirements

### Requirement 1: Line Number Gutter Display

**User Story:** As a user, I want to see line numbers to the left of the text content, so that I can identify which line I am looking at.

#### Acceptance Criteria

1. WHEN an active tab has view rows displayed, THE Text_View_Area component SHALL render a Line_Number_Gutter to the left of the text content area showing 1-based line numbers for each visible Logical_Line; numbering applies only to rows backed by content returned from the backend (rows with no backing content receive no gutter cell); the first displayed line number equals Start_Line + 1 and subsequent Logical_Lines increment by 1; the digits_in_total_lines value used for Gutter_Width calculation is independent of the first displayed line number
2. THE Line_Number_Gutter SHALL be rendered as a fixed-position column that does not scroll horizontally when the text content is scrolled (remains visible at all times)
3. THE Line_Number_Gutter SHALL right-align line numbers within the gutter column and use the same monospace font-family and font-size as the text content
4. THE Gutter_Width SHALL be computed as: (number of digits in Total_Logical_Lines for the active tab's session) multiplied by Char_Metrics width, plus 8 pixels of left padding and 8 pixels of right padding; the minimum Gutter_Width SHALL accommodate at least one digit plus the padding; this ensures gutter width remains stable during scrolling regardless of which lines are currently visible and regardless of whether Wrap_Mode is on or off
5. WHEN the viewport scrolls vertically (Start_Line changes), THE Line_Number_Gutter SHALL update to display line numbers corresponding to the new visible Logical_Lines
6. WHILE no active tab exists or no view rows are loaded, THE Line_Number_Gutter SHALL not be rendered
7. IF the number of view rows displayed is fewer than Row_Count (partial last page), THEN THE Line_Number_Gutter SHALL render line numbers only for the rows that contain actual Logical_Lines and SHALL leave remaining vertical space empty (no placeholder numbers)

### Requirement 2: Line Numbers in Non-Wrapped Mode

**User Story:** As a user, I want each displayed row to show its corresponding line number in non-wrapped mode, so that line numbers map directly to file lines.

#### Acceptance Criteria

1. WHILE Wrap_Mode is off, THE Line_Number_Gutter SHALL display one 1-based line number per Visual_Row, where the line number for row index i (ranging from 0 to the number of rows actually returned minus 1) is (Start_Line + i + 1), with the first line of the file displaying as line 1; IF the backend returns fewer rows than Row_Count (e.g., near end of file), THEN the Line_Number_Gutter SHALL render only as many line number cells as rows returned, with no gutter cells for the remaining empty space
2. WHILE Wrap_Mode is off, THE Text_View_Area component SHALL continue using the existing rectangular view request (View_Session_ID, startLine, startCol, rowCount, colCount) to fetch content from the backend
3. WHILE Wrap_Mode is off, THE Line_Number_Gutter SHALL remain fixed at the left edge of the Text_View_Area regardless of the current Start_Col value (horizontal scroll position does not affect gutter visibility or position)

### Requirement 3: Line Numbers in Wrapped Mode

**User Story:** As a user, I want line numbers to remain visible for wrapped lines as long as any part of that line is on screen, so that I can always identify which logical line I am reading.

#### Acceptance Criteria

1. WHILE Wrap_Mode is on AND the first Visual_Row of a Logical_Line is visible in the viewport, THE Line_Number_Gutter SHALL display the 1-based line number only on that first Visual_Row; all other Visual_Rows of the same Logical_Line visible in the viewport SHALL show an empty gutter cell of the same height as a Visual_Row (no number, no placeholder character)
2. WHILE Wrap_Mode is on AND the first Visual_Row of a Logical_Line is not visible in the viewport but one or more subsequent Visual_Rows of that Logical_Line remain visible, THE Line_Number_Gutter SHALL display that Logical_Line's 1-based line number on the topmost visible Visual_Row of that Logical_Line; all other visible Visual_Rows of the same Logical_Line below the topmost SHALL show an empty gutter cell
3. WHILE Wrap_Mode is on AND a single Logical_Line wraps to more Visual_Rows than fit in the viewport (Row_Count), THE Line_Number_Gutter SHALL display that line's number on the topmost visible Visual_Row (row index 0 in the viewport) with all remaining visible Visual_Rows of that line showing empty gutter cells
4. WHILE Wrap_Mode is on, THE Line_Number_Gutter SHALL remain fixed at the left edge of the Text_View_Area (no horizontal scrolling applies in wrapped mode since the horizontal scrollbar is hidden and lines wrap at the Col_Count boundary)

### Requirement 4: Wrap Mode Toggle

**User Story:** As a user, I want a checkbox on the status bar to toggle line wrapping on and off, so that I can choose my preferred viewing mode.

#### Acceptance Criteria

1. THE Status_Bar component SHALL render a Wrap_Checkbox labeled "Wrap" that toggles Wrap_Mode between on and off states
2. THE Wrap_Checkbox SHALL default to unchecked (Wrap_Mode off / non-wrapped) when the application starts
3. WHEN the user toggles the Wrap_Checkbox from off to on, THE Text_View_Area component SHALL reset Start_Col to 0, re-render the current view in wrapped mode breaking lines at the Col_Count boundary, and send a wrapped-mode view request to the backend using the active tab's current Start_Line, Character_Offset of 0, and Character_Count of Col_Count multiplied by Row_Count (this is the sole request triggered by the toggle; no additional refresh request shall be sent)
4. WHEN the user toggles the Wrap_Checkbox from on to off, THE Text_View_Area component SHALL re-render the current view in non-wrapped mode with Start_Col restored to 0 and send a standard rectangular view request to the backend using the active tab's current Start_Line, Start_Col of 0, and current Row_Count and Col_Count (this is the sole request triggered by the toggle; no additional refresh request shall be sent)
5. THE Wrap_Mode state SHALL be stored per-application (not per-tab); toggling wrap mode SHALL affect all tabs uniformly such that non-active tabs are marked as requiring a content refresh and SHALL receive a new view request (in the appropriate format for the current Wrap_Mode) when they become the active tab; on successful response, the current content SHALL be replaced with the new response data
6. IF no active tab exists when Wrap_Mode is toggled, THEN THE frontend SHALL update the Wrap_Mode state without sending any backend request
7. IF the backend returns an error response to the mode-switch view request, THEN THE Text_View_Area component SHALL keep the previously displayed rows visible and display the error message simultaneously with the old content rather than replacing it; error messages SHALL only be displayed when the backend explicitly returns an error response (not for other conditions); the new response data SHALL replace the current content only on successful responses

### Requirement 5: Wrapped Mode Content Request

**User Story:** As a developer, I want the frontend to request text by start line, character offset, and character count in wrapped mode, so that the backend can return the exact slice needed for wrapped display.

#### Acceptance Criteria

1. WHILE Wrap_Mode is on, THE frontend SHALL send a wrapped-mode view request to the backend containing: View_Session_ID, start Logical_Line number (zero-based), Character_Offset from the beginning of that line (zero-based, representing how many characters of the line have been scrolled above the viewport), and Character_Count (total number of content characters to retrieve for display); each character is one UTF-16 code unit consistent with the backend's .NET char counting
2. THE Character_Count SHALL be computed as: Col_Count multiplied by Row_Count (the total number of character cells visible in the viewport)
3. WHEN the viewport is scrolled such that the start of a Logical_Line is above the visible area (partial line at top), THE frontend SHALL set Character_Offset to the number of characters of that line already scrolled past (multiples of Col_Count for full wrapped rows scrolled above)
4. THE wrapped-mode view request payload SHALL be formatted as a newline-delimited string (delimiter: U+000A) with exactly five fields in order: View_Session_ID, "W" (literal single character indicating wrapped mode), startLine, characterOffset, characterCount; the startLine, characterOffset, and characterCount values SHALL be encoded as decimal integer strings containing only ASCII digits 0-9 with no leading zeros except for the value "0" itself, no whitespace, and no sign characters, within the range 0 to 2,147,483,647; IF viewport dimensions would cause Character_Count (Col_Count × Row_Count) to exceed 2,147,483,647, THEN THE frontend SHALL cap Character_Count at 2,147,483,647
5. WHILE Wrap_Mode is on, WHEN the active tab changes or viewport dimensions change, THE frontend SHALL send a new wrapped-mode view request with the newly active tab's View_Session_ID, that tab's current start Logical_Line, Character_Offset, and the recomputed Character_Count; IF a wrapped-mode view request is already pending for the active tab, THEN THE frontend SHALL cancel the pending request before sending the new one (latest-wins)
6. IF a wrapped-mode view request fails (error response received from backend), THEN THE frontend SHALL keep the previously displayed rows visible and display the error description separately from the content area; this applies to any error display scenario in wrapped mode regardless of error source

### Requirement 6: Backend Wrapped Mode Response

**User Story:** As a developer, I want the FileViewService to return a character-count-based slice that does not count newline delimiters toward the requested count, so that the frontend receives exactly the content needed for wrapped display.

#### Acceptance Criteria

1. WHEN a wrapped-mode view request is received (identified by the "W" marker in the payload) with startLine ≥ 0, Character_Offset ≥ 0, and Character_Count ≥ 1, THE File_View_Service SHALL read starting from the specified Logical_Line at the specified Character_Offset and return up to Character_Count content characters, where each content character is one .NET char (UTF-16 code unit) consistent with the existing column-counting rule
2. THE File_View_Service SHALL NOT count newline delimiter characters (\n, \r\n, \r) toward the Character_Count limit; delimiter characters SHALL be included in the response output when encountered (so the frontend can detect line boundaries)
3. IF the Character_Offset exceeds the content length of the specified start line (excluding delimiter), THEN THE File_View_Service SHALL advance to subsequent lines, subtracting each skipped line's content length (excluding delimiter) from the remaining offset without counting delimiters between lines toward offset consumption, until the offset is satisfied or the file ends
4. IF the end of the file is reached before Character_Count content characters are collected, THEN THE File_View_Service SHALL return all remaining content characters (response may be shorter than requested)
5. THE wrapped-mode response payload SHALL be a single string containing the collected characters (including any encountered delimiters) so the frontend can split and wrap them at Col_Count boundaries
6. IF the start line is beyond the file's total line count, THEN THE File_View_Service SHALL return an empty string
7. IF a wrapped-mode view request is received with startLine < 0, Character_Offset < 0, or Character_Count < 1, THEN THE File_View_Service SHALL return an error response as a string with the format "ERROR: {paramName} out of range" where {paramName} is the first invalid parameter name (startLine, characterOffset, or characterCount); the frontend SHALL detect error responses by checking whether the response string starts with the prefix "ERROR:"
8. IF the file scan is still in progress and the specified start line is beyond the currently scanned range, THEN THE File_View_Service SHALL return an empty string (consistent with scan-in-progress behavior for standard view requests)

### Requirement 7: Wrapped Mode Rendering

**User Story:** As a user, I want wrapped text to break at the viewport column boundary so that I can read long lines without horizontal scrolling.

#### Acceptance Criteria

1. WHILE Wrap_Mode is on, THE Text_View_Area component SHALL split the response content into Visual_Rows of at most Col_Count characters each (where each character is one UTF-16 code unit), breaking at Col_Count boundaries (hard wrap, not word wrap); newline delimiter characters encountered in the response SHALL be consumed as line-boundary markers and SHALL NOT be counted toward the Col_Count character limit or rendered as visible characters
2. WHILE Wrap_Mode is on, THE Text_View_Area component SHALL render each Visual_Row as a separate block-level element using the same monospace font-family and font-size as non-wrapped mode
3. WHILE Wrap_Mode is on, THE horizontal scrollbar SHALL be hidden (no horizontal scrolling needed since all content wraps within the viewport width)
4. WHILE Wrap_Mode is on, THE vertical scrollbar Scrollbar_Max SHALL be computed by the frontend as the sum of ceil(Char_Length / Col_Count) for each Logical_Line in the file (where Char_Length is the content length excluding the delimiter, and lines with zero content length contribute 1 Visual_Row); this value SHALL be recomputed whenever Col_Count changes or the file's line metadata is updated
5. WHEN a newline delimiter is encountered in the response content, THE Text_View_Area component SHALL end the current Visual_Row at that point (even if fewer than Col_Count characters have been placed) and begin a new Logical_Line on the next Visual_Row
6. IF the response content is empty (zero content characters), THEN THE Text_View_Area component SHALL render zero Visual_Rows (empty content region, no placeholder text)

### Requirement 8: Wrapped Mode Vertical Scrolling

**User Story:** As a user, I want to scroll vertically through wrapped content one Visual_Row at a time, so that navigation feels natural in wrapped mode.

#### Acceptance Criteria

1. WHILE Wrap_Mode is on, THE vertical scroll actions (wheel, arrow keys, thumb drag) SHALL advance the viewport by Visual_Rows rather than Logical_Lines, using Character_Offset and start Logical_Line to track position
2. WHILE Wrap_Mode is on, WHEN the user scrolls down by one Visual_Row, THE frontend SHALL increase Character_Offset by Col_Count characters; IF the new Character_Offset equals or exceeds the content length of the current Logical_Line (excluding delimiter), THEN THE frontend SHALL advance to the next Logical_Line and set Character_Offset to 0 (empty lines with content length 0 always advance to the next line immediately), and send a new wrapped-mode view request
3. WHILE Wrap_Mode is on, WHEN the user scrolls up by one Visual_Row, THE frontend SHALL decrease Character_Offset by Col_Count characters; IF the resulting Character_Offset is negative, THEN THE frontend SHALL move to the previous Logical_Line and set Character_Offset to (floor((previous line's content length - 1) / Col_Count) * Col_Count) which is the start of the last wrapped row of that line (for empty lines with content length 0, Character_Offset SHALL be 0), and send a new wrapped-mode view request
4. WHILE Wrap_Mode is on, IF the viewport is at the top of the file (Logical_Line is 0 AND Character_Offset is 0) AND the user scrolls up, THEN THE frontend SHALL not change position and SHALL NOT send a wrapped-mode view request
5. WHILE Wrap_Mode is on, IF the viewport is at the last Visual_Row of the file AND the user scrolls down, THEN THE frontend SHALL not change position and SHALL NOT send a wrapped-mode view request
6. WHILE Wrap_Mode is on, THE mouse wheel Scroll_Step SHALL be 3 Visual_Rows per tick (consistent with non-wrapped mode behavior); the Character_Offset adjustment and line-boundary crossing logic from criteria 2 and 3 SHALL be applied iteratively for each Visual_Row in the step
7. WHILE Wrap_Mode is on, THE arrow key Scroll_Step SHALL be 1 Visual_Row per key press

### Requirement 9: Gutter Interaction with Viewport Measurement

**User Story:** As a developer, I want the line number gutter width to be excluded from the text content measurement area, so that Col_Count accurately reflects the available text display width.

#### Acceptance Criteria

1. WHEN Line_Number_Gutter is rendered (its DOM element has a non-zero client width), THE Text_View_Area component SHALL compute Col_Count as floor((available_pixel_width − Gutter_Width) / Char_Metrics width), with a minimum value of 1, where Gutter_Width is the Line_Number_Gutter element's client width in pixels
2. WHEN Gutter_Width changes (due to Total_Logical_Lines digit count increasing or decreasing, e.g. when file scan discovers more lines), THE Text_View_Area component SHALL recompute Col_Count using the new Gutter_Width and, IF Col_Count changed, trigger a new View_Request with the updated Col_Count for the active tab
3. THE Row_Count computation SHALL remain unchanged by the presence or absence of Line_Number_Gutter (gutter does not affect vertical measurement)
4. WHILE Line_Number_Gutter is not rendered (no active tab or gutter feature disabled), THE Text_View_Area component SHALL compute Col_Count using the full available pixel width (Gutter_Width treated as 0)
