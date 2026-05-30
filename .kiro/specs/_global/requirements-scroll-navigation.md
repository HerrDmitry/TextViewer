# Scroll Navigation — Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

Scroll Navigation makes the existing scrollbar UI interactive. Builds on text-handling (Req 9–11: scrollbar rendering, progressive updates, polling). This feature adds thumb dragging, mouse wheel scrolling, arrow key navigation, proportional thumb sizing/positioning, and latest-wins view request cancellation.

Depends on: text-handling (scrollbar rendering, TabViewState, view request orchestration).

## Glossary

- **Start_Line**: Zero-based index of first visible line; determines vertical scroll position
- **Start_Col**: Zero-based index of first visible column; determines horizontal scroll position
- **Thumb_Position**: Pixel offset of scrollbar thumb from track start, proportional to Start_Line/Start_Col relative to Scrollbar_Max
- **Thumb_Size**: Pixel length of scrollbar thumb, proportional to viewport rows/cols relative to total content extent
- **Track_Length**: Usable pixel length of scrollbar track (total track pixels minus Thumb_Size)
- **Scroll_Step**: Lines/columns per discrete scroll event; 1 for arrow keys, 3 for wheel
- **Drag_State**: Transient state during thumb drag — captures initial mouse position and initial Start_Line/Start_Col at drag start
- **Viewport_Ratio**: Ratio of viewport size to total content size (Row_Count / Scrollbar_Max for vertical)

## Requirements

### Requirement 1: Vertical Thumb Dragging

**User Story:** As a user, I want to drag the vertical scrollbar thumb to navigate to any line in the file.

#### Acceptance Criteria

1. WHEN mousedown on vertical Scrollbar_Thumb, THE component SHALL enter Drag_State capturing initial mouse Y and current Start_Line
2. WHILE Drag_State active (vertical), THE component SHALL compute Start_Line = clamp(initial_Start_Line + round(deltaY / Track_Length × (Scrollbar_Max − Row_Count)), 0, Scrollbar_Max − Row_Count) on each mousemove
3. WHILE Drag_State active, THE component SHALL update Thumb_Position visually on each mousemove without waiting for backend response (optimistic)
4. WHEN mouseup while Drag_State active, THE component SHALL exit Drag_State and send View_Request with final Start_Line; IF request fails, accept out-of-sync state without retry
5. IF Scrollbar_Max ≤ Row_Count OR file empty, THEN vertical scrollbar SHALL remain non-interactive (no Drag_State on mousedown)
6. WHILE Drag_State active, THE component SHALL apply `user-select: none` to document body

### Requirement 2: Horizontal Thumb Dragging

**User Story:** As a user, I want to drag the horizontal scrollbar thumb to navigate to any column.

#### Acceptance Criteria

1. WHEN mousedown on horizontal Scrollbar_Thumb, THE component SHALL enter Drag_State capturing initial mouse X and current Start_Col
2. WHILE Drag_State active (horizontal), THE component SHALL compute Start_Col = clamp(initial_Start_Col + round(deltaX / Track_Length × (Scrollbar_Max − Col_Count)), 0, Scrollbar_Max − Col_Count) on each mousemove
3. WHILE Drag_State active, THE component SHALL update Thumb_Position visually on each mousemove without waiting for backend response
4. WHEN mouseup while Drag_State active, THE component SHALL exit Drag_State and send View_Request with final Start_Col; IF request fails, accept out-of-sync state without retry
5. IF Scrollbar_Max ≤ Col_Count OR file empty, THEN horizontal scrollbar SHALL remain non-interactive
6. WHILE Drag_State active, THE component SHALL apply `user-select: none` to document body

### Requirement 3: Mouse Wheel Scrolling

**User Story:** As a user, I want to scroll using the mouse wheel for natural navigation.

#### Acceptance Criteria

1. WHEN wheel event with deltaY ≠ 0, THE component SHALL compute Start_Line = clamp(current + sign(deltaY) × 3, 0, Scrollbar_Max − Row_Count)
2. WHEN wheel event with deltaX ≠ 0, THE component SHALL compute Start_Col = clamp(current + sign(deltaX) × 3, 0, Scrollbar_Max − Col_Count)
3. WHEN computed position differs from current (including both axes from single event), THE component SHALL send single View_Request with new Start_Line, Start_Col, Row_Count, Col_Count
4. IF computed position equals current (at boundary), THE component SHALL NOT send View_Request
5. THE component SHALL call preventDefault() on wheel events to suppress native scrolling
6. IF Scrollbar_Max for vertical ≤ Row_Count, THEN vertical wheel events produce no Start_Line change
7. IF Scrollbar_Max for horizontal ≤ Col_Count, THEN horizontal wheel events produce no Start_Col change

### Requirement 4: Arrow Key Navigation

**User Story:** As a user, I want to use arrow keys to scroll one line/column at a time for precise navigation.

#### Acceptance Criteria

1. WHEN ArrowDown pressed with focus, THE component SHALL compute Start_Line = clamp(current + 1, 0, Scrollbar_Max − Row_Count)
2. WHEN ArrowUp pressed with focus, THE component SHALL compute Start_Line = clamp(current − 1, 0, Scrollbar_Max − Row_Count)
3. WHEN ArrowRight pressed with focus, THE component SHALL compute Start_Col = clamp(current + 1, 0, Scrollbar_Max − Col_Count)
4. WHEN ArrowLeft pressed with focus, THE component SHALL compute Start_Col = clamp(current − 1, 0, Scrollbar_Max − Col_Count)
5. WHEN computed position differs from current, THE component SHALL send View_Request
6. IF computed position equals current (at boundary), THE component SHALL NOT send View_Request
7. THE component SHALL call preventDefault() on arrow keys only when active tab exists
8. WHILE no active tab exists, THE component SHALL ignore arrow keys entirely without preventDefault()

### Requirement 5: Thumb Position Calculation

**User Story:** As a user, I want the thumb position to reflect my current position in the file.

#### Acceptance Criteria

1. THE component SHALL compute vertical Thumb_Position as: pixel_offset = (Start_Line / (Scrollbar_Max − Row_Count)) × Track_Length
2. THE component SHALL compute horizontal Thumb_Position as: pixel_offset = (Start_Col / (Scrollbar_Max − Col_Count)) × Track_Length
3. WHEN View_Response received and Start_Line/Start_Col updated, THE component SHALL recompute Thumb_Position
4. IF Start_Line = 0, THEN vertical Thumb_Position SHALL be 0 (top)
5. IF Start_Line = Scrollbar_Max − Row_Count, THEN vertical Thumb_Position SHALL be at track bottom
6. IF Scrollbar_Max ≤ Row_Count (vertical) or Col_Count (horizontal), THEN Thumb_Position forced to 0

### Requirement 6: Thumb Size Calculation

**User Story:** As a user, I want the thumb size to indicate how much of the file is visible.

#### Acceptance Criteria

1. THE component SHALL compute vertical Thumb_Size = max(20px, (Row_Count / Scrollbar_Max) × track_pixel_height)
2. THE component SHALL compute horizontal Thumb_Size = max(20px, (Col_Count / Scrollbar_Max) × track_pixel_width)
3. WHEN Scrollbar_Max or Row_Count/Col_Count changes, THE component SHALL recompute Thumb_Size
4. IF Scrollbar_Max ≤ Row_Count (vertical) or Col_Count (horizontal), THEN Thumb_Size SHALL equal full track length
5. THE Thumb_Size SHALL be applied as inline style (height for vertical, width for horizontal)

### Requirement 7: View Request on Scroll

**User Story:** As a developer, I want scroll actions to trigger view requests with correct startLine/startCol.

#### Acceptance Criteria

1. WHEN scroll action produces new Start_Line or Start_Col, THE frontend SHALL send View_Request with active tab's View_Session_ID, new Start_Line, Start_Col, current Row_Count, Col_Count
2. IF View_Request already pending for active tab, THE frontend SHALL cancel pending request and send new one with latest position (latest-wins)
3. WHEN successful View_Response received for scroll-triggered request, THE component SHALL replace displayed rows with new rows
4. THE frontend SHALL store Start_Line/Start_Col per tab in TabViewState for tab-switch restoration
5. WHEN active tab changes to tab with stored Start_Line/Start_Col, THE component SHALL restore Thumb_Position without sending new View_Request (cached rows already displayed)

### Requirement 8: Display Updated Text After Scroll

**User Story:** As a user, I want to see new content after scrolling.

#### Acceptance Criteria

1. WHEN successful View_Response received for scroll request, THE component SHALL replace displayed rows with new rows
2. THE component SHALL render new rows using same monospace rendering as initial view
3. WHILE scroll View_Request pending, THE component SHALL continue displaying previous rows (no blank flash)
4. IF error View_Response received for scroll request, THE component SHALL keep previous rows visible and display error separately
