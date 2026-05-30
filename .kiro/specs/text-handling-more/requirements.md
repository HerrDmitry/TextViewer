# Requirements Document

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

Scroll Navigation feature. Builds on existing text-handling infrastructure (scrollbar UI rendered, thumb static). This feature makes scrollbars interactive: thumb dragging, mouse wheel, arrow keys → calculate new startLine/startCol → send get-view → display updated text. Also computes thumb position and size proportionally.

Depends on: text-handling spec (Req 1–11 already shipped — measurement, view request/response, scrollbar rendering, progressive updates).

## Glossary

- **Start_Line**: Zero-based index of the first visible line in the viewport; determines vertical scroll position
- **Start_Col**: Zero-based index of the first visible column in the viewport; determines horizontal scroll position
- **Thumb_Position**: Pixel offset of the scrollbar thumb from the start of the track, proportional to Start_Line/Start_Col relative to Scrollbar_Max
- **Thumb_Size**: Pixel length of the scrollbar thumb, proportional to viewport rows/cols relative to total content extent
- **Track_Length**: Usable pixel length of the scrollbar track (total track pixels minus Thumb_Size)
- **Scroll_Step**: Number of lines or columns to advance per discrete scroll event (arrow key or wheel tick); 1 for arrow keys, 3 for wheel
- **Drag_State**: Transient state during thumb drag — captures initial mouse position and initial Start_Line/Start_Col at drag start
- **Viewport_Ratio**: Ratio of viewport size to total content size (Row_Count / Scrollbar_Max for vertical, Col_Count / Scrollbar_Max for horizontal); used for thumb size calculation

## Requirements

### Requirement 1: Vertical Thumb Dragging

**User Story:** As a user, I want to drag the vertical scrollbar thumb to navigate to any line in the file, so that I can quickly jump to a specific position.

#### Acceptance Criteria

1. WHEN the user presses mousedown on the vertical Scrollbar_Thumb, THE Text_View_Area component SHALL enter Drag_State capturing the initial mouse Y coordinate and the current Start_Line
2. WHILE Drag_State is active for the vertical scrollbar, THE Text_View_Area component SHALL compute a new Start_Line on each mousemove event as: Start_Line = clamp(initial_Start_Line + round(deltaY / Track_Length * (Scrollbar_Max - Row_Count)), 0, Scrollbar_Max - Row_Count); computation SHALL occur only in response to actual mousemove events (not continuously)
3. WHILE Drag_State is active, THE Text_View_Area component SHALL update the vertical Thumb_Position visually on each mousemove without waiting for a backend response (optimistic thumb repositioning)
4. WHEN the user releases the mouse (mouseup) while Drag_State is active, THE Text_View_Area component SHALL exit Drag_State and send a View_Request with the final computed Start_Line, current Start_Col, and current Row_Count and Col_Count; IF the View_Request fails, THE system SHALL accept that the displayed view may be out of sync with the scroll position without retrying
5. IF Scrollbar_Max is less than or equal to Row_Count (all content fits in viewport) OR the file is empty (zero lines), THEN THE vertical scrollbar SHALL remain non-interactive (mousedown on thumb produces no Drag_State)
6. WHILE Drag_State is active, THE Text_View_Area component SHALL apply `user-select: none` to the document body to prevent text selection during drag

### Requirement 2: Horizontal Thumb Dragging

**User Story:** As a user, I want to drag the horizontal scrollbar thumb to navigate to any column in the file, so that I can view wide lines.

#### Acceptance Criteria

1. WHEN the user presses mousedown on the horizontal Scrollbar_Thumb, THE Text_View_Area component SHALL enter Drag_State capturing the initial mouse X coordinate and the current Start_Col
2. WHILE Drag_State is active for the horizontal scrollbar, THE Text_View_Area component SHALL compute a new Start_Col on each mousemove as: Start_Col = clamp(initial_Start_Col + round(deltaX / Track_Length * (Scrollbar_Max - Col_Count)), 0, Scrollbar_Max - Col_Count)
3. WHILE Drag_State is active, THE Text_View_Area component SHALL update the horizontal Thumb_Position visually on each mousemove without waiting for a backend response
4. WHEN the user releases the mouse (mouseup) while Drag_State is active, THE Text_View_Area component SHALL exit Drag_State and send a View_Request with current Start_Line, the final computed Start_Col, and current Row_Count and Col_Count; IF the View_Request fails, THE system SHALL accept that the displayed view may be out of sync with the scroll position without retrying
5. IF Scrollbar_Max is less than or equal to Col_Count (all content fits in viewport) OR the file is empty, THEN THE horizontal scrollbar SHALL remain non-interactive (mousedown on thumb produces no Drag_State)
6. WHILE Drag_State is active, THE Text_View_Area component SHALL apply `user-select: none` to the document body to prevent text selection during drag

### Requirement 3: Mouse Wheel Scrolling

**User Story:** As a user, I want to scroll through the file using the mouse wheel, so that I can navigate content naturally without using the scrollbar.

#### Acceptance Criteria

1. WHEN a wheel event is received on the Text_View_Area with deltaY != 0, THE Text_View_Area component SHALL compute a new Start_Line as: Start_Line = clamp(current_Start_Line + sign(deltaY) * Scroll_Step, 0, Scrollbar_Max - Row_Count), where Scroll_Step for wheel is 3 lines
2. WHEN a wheel event is received on the Text_View_Area with deltaX != 0, THE Text_View_Area component SHALL compute a new Start_Col as: Start_Col = clamp(current_Start_Col + sign(deltaX) * Scroll_Step, 0, Scrollbar_Max - Col_Count), where Scroll_Step for horizontal wheel is 3 columns
3. WHEN the computed Start_Line or Start_Col differs from the current value (including when both axes change simultaneously from a single wheel event), THE Text_View_Area component SHALL send a single View_Request with the new Start_Line, new Start_Col, and current Row_Count and Col_Count
4. IF the computed Start_Line or Start_Col equals the current value (already at boundary), THEN THE Text_View_Area component SHALL not send a View_Request
5. THE Text_View_Area component SHALL call preventDefault() on the wheel event to suppress native scrolling behavior
6. IF Scrollbar_Max for the vertical axis is less than or equal to Row_Count, THEN vertical wheel events SHALL produce no Start_Line change
7. IF Scrollbar_Max for the horizontal axis is less than or equal to Col_Count, THEN horizontal wheel events SHALL produce no Start_Col change

### Requirement 4: Arrow Key Navigation

**User Story:** As a user, I want to use arrow keys to scroll through the file one line or column at a time, so that I can navigate precisely.

#### Acceptance Criteria

1. WHEN the ArrowDown key is pressed while Text_View_Area has focus, THE Text_View_Area component SHALL compute a new Start_Line as: Start_Line = clamp(current_Start_Line + 1, 0, Scrollbar_Max - Row_Count)
2. WHEN the ArrowUp key is pressed while Text_View_Area has focus, THE Text_View_Area component SHALL compute a new Start_Line as: Start_Line = clamp(current_Start_Line - 1, 0, Scrollbar_Max - Row_Count)
3. WHEN the ArrowRight key is pressed while Text_View_Area has focus, THE Text_View_Area component SHALL compute a new Start_Col as: Start_Col = clamp(current_Start_Col + 1, 0, Scrollbar_Max - Col_Count)
4. WHEN the ArrowLeft key is pressed while Text_View_Area has focus, THE Text_View_Area component SHALL compute a new Start_Col as: Start_Col = clamp(current_Start_Col - 1, 0, Scrollbar_Max - Col_Count)
5. WHEN the computed Start_Line or Start_Col differs from the current value, THE Text_View_Area component SHALL send a View_Request with the new Start_Line, Start_Col, and current Row_Count and Col_Count
6. IF the computed value equals the current value (already at boundary), THEN THE Text_View_Area component SHALL not send a View_Request
7. THE Text_View_Area component SHALL call preventDefault() on arrow key events to suppress default browser scrolling only when an active tab exists
8. WHILE no active tab exists, THE Text_View_Area component SHALL ignore arrow key events entirely without calling preventDefault()

### Requirement 5: Thumb Position Calculation

**User Story:** As a user, I want the scrollbar thumb position to reflect my current position in the file, so that I can see where I am relative to the whole document.

#### Acceptance Criteria

1. THE Text_View_Area component SHALL compute vertical Thumb_Position as: pixel_offset = (Start_Line / (Scrollbar_Max - Row_Count)) * Track_Length, where Track_Length = track_pixel_height - Thumb_Size_pixels
2. THE Text_View_Area component SHALL compute horizontal Thumb_Position as: pixel_offset = (Start_Col / (Scrollbar_Max - Col_Count)) * Track_Length, where Track_Length = track_pixel_width - Thumb_Size_pixels
3. WHEN a View_Response is received and Start_Line or Start_Col is updated, THE Text_View_Area component SHALL recompute and apply the corresponding Thumb_Position
4. IF Start_Line is 0, THEN THE vertical Thumb_Position SHALL be 0 (thumb at top of track)
5. IF Start_Line equals Scrollbar_Max - Row_Count, THEN THE vertical Thumb_Position SHALL place the thumb at the bottom of the track (pixel_offset = Track_Length)
6. IF Scrollbar_Max is less than or equal to Row_Count (vertical) or Col_Count (horizontal), THEN THE Thumb_Position variable SHALL be forced to 0 in the data model (thumb at start, no scrollable range)

### Requirement 6: Thumb Size Calculation

**User Story:** As a user, I want the scrollbar thumb size to indicate how much of the file is visible, so that I can gauge the viewport proportion.

#### Acceptance Criteria

1. THE Text_View_Area component SHALL compute vertical Thumb_Size in pixels as: max(min_thumb_size, (Row_Count / Scrollbar_Max) * track_pixel_height), where min_thumb_size is 20 pixels
2. THE Text_View_Area component SHALL compute horizontal Thumb_Size in pixels as: max(min_thumb_size, (Col_Count / Scrollbar_Max) * track_pixel_width), where min_thumb_size is 20 pixels
3. WHEN Scrollbar_Max or Row_Count or Col_Count changes, THE Text_View_Area component SHALL recompute the corresponding Thumb_Size
4. IF Scrollbar_Max is 0 or less than or equal to Row_Count (vertical) or Col_Count (horizontal), THEN THE Thumb_Size SHALL equal the full track length (thumb fills entire track, indicating all content visible); the minimum Thumb_Size of 20 pixels still applies in all other cases
5. THE Thumb_Size SHALL be applied as an inline style (height for vertical, width for horizontal) on the Scrollbar_Thumb element

### Requirement 7: View Request on Scroll

**User Story:** As a developer, I want scroll actions to trigger view requests with the correct startLine and startCol, so that the backend returns the right content slice.

#### Acceptance Criteria

1. WHEN a scroll action (drag release, wheel, or arrow key) produces a new Start_Line or Start_Col, THE frontend SHALL send a View_Request via Message_Bus with the active tab's View_Session_ID, the new Start_Line, new Start_Col, and current Row_Count and Col_Count
2. IF a View_Request is already pending for the active tab (pendingCorrelationId is non-null), THEN THE frontend SHALL cancel the pending request and send a new View_Request with the latest Start_Line and Start_Col (latest-wins for scroll)
3. WHEN a successful View_Response is received for a scroll-triggered View_Request, THE Text_View_Area component SHALL immediately replace the currently displayed rows with the new rows from the response
4. THE frontend SHALL store the current Start_Line and Start_Col per tab in TabViewState so that tab switches restore the correct scroll position
5. WHEN the active tab changes to a tab with stored Start_Line and Start_Col, THE Text_View_Area component SHALL restore the Thumb_Position to reflect the stored scroll position without sending a new View_Request (cached rows already displayed per existing Requirement 5.5 in text-handling)

### Requirement 8: Display Updated Text After Scroll

**User Story:** As a user, I want to see the new text content after scrolling, so that I can read the file at the new position.

#### Acceptance Criteria

1. WHEN a successful View_Response is received for a scroll-triggered View_Request, THE Text_View_Area component SHALL replace the currently displayed rows with the new rows from the response
2. THE Text_View_Area component SHALL render the new rows using the same monospace rendering as the initial view (one block-level element per row, verbatim content, overflow hidden)
3. WHILE a scroll-triggered View_Request is pending, THE Text_View_Area component SHALL continue displaying the previous rows (no blank flash or loading indicator)
4. IF an error View_Response is received for a scroll-triggered View_Request, THEN THE Text_View_Area component SHALL keep the previous rows visible and display the error message separately (e.g., in a status area or overlay) rather than replacing the content

