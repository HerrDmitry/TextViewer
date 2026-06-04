# Viewer UI Shell — Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

Viewer UI Shell feature. Defines the top-level layout of the Angular frontend: a tabbed text viewer area occupying most of the screen, a drop-down menu bar at the top, and a status bar at the bottom. Tabs represent open files; the menu provides Open and Exit actions; the status bar displays the active file path. When no file is open, the viewer area shows a placeholder prompt.

## Glossary

- **UI_Shell**: The top-level Angular layout component containing Menu_Bar, Tab_Container, Text_View_Area, and Status_Bar
- **Menu_Bar**: A horizontal bar at the top of the window containing drop-down menus
- **File_Menu**: A drop-down menu within Menu_Bar containing file-related actions (Open, Exit)
- **Tab_Container**: A region holding Tab_Headers; can be positioned at the top or bottom of the Text_View_Area
- **Tab_Header**: A clickable label representing one open file, displaying the file name and a Close_Button
- **Close_Button**: A button on each Tab_Header that closes the associated tab
- **Text_View_Area**: The main content region displaying the Empty_State_Prompt when no tabs are open (file content rendering is handled by a separate feature)
- **Empty_State_Prompt**: The placeholder text "Ctrl-O to open a file" shown when no tabs are open
- **Status_Bar**: A horizontal bar at the bottom of the window displaying contextual information
- **Active_Tab**: The currently selected tab whose content is displayed in Text_View_Area

## Requirements

### Requirement 1: Shell Layout Structure

**User Story:** As a user, I want the application to have a clear visual structure with a menu, content area, and status bar, so that I can navigate and understand the interface.

#### Acceptance Criteria

1. THE UI_Shell SHALL render components in the following vertical order from top to bottom: Menu_Bar, Tab_Container (default position), Text_View_Area, Status_Bar
2. THE Text_View_Area SHALL dynamically occupy all remaining vertical space between Menu_Bar, Tab_Container, and Status_Bar at any window size
3. THE UI_Shell SHALL fill the entire Photino_Window viewport with no outer margins and SHALL NOT produce viewport-level scrollbars (child components may scroll independently)

### Requirement 2: Drop-Down Menu

**User Story:** As a user, I want a menu with Open and Exit options, so that I can open files and close the application using the menu.

#### Acceptance Criteria

1. THE Menu_Bar SHALL contain a single menu labeled "File"
2. WHEN the user clicks the "File" label in Menu_Bar, THE Menu_Bar SHALL expand File_Menu displaying its items
3. THE File_Menu SHALL contain exactly two items in order: "Open..." and "Exit"
4. WHEN the user selects "Open..." from File_Menu, THE Menu_Bar SHALL immediately collapse File_Menu (synchronous DOM hide) before triggering the open-file action, ensuring the dropdown is visually removed even if the native file dialog blocks the UI thread
5. WHEN the user selects "Exit" from File_Menu, THE UI_Shell SHALL send an "exit" message via Message_Bus_Client to the Backend, which SHALL close the Photino_Window
6. WHEN the user presses Ctrl+O on Windows/Linux or Cmd+O on macOS, THE UI_Shell SHALL trigger the open-file action identical to selecting "Open..." from File_Menu, regardless of current UI focus or interaction state
7. WHILE the UI_Shell is awaiting a response from a previous open-file request (state begins when the open-file message is sent via Message_Bus_Client and ends when a correlated response is received or the request times out per Message_Bus timeout policy), THE UI_Shell SHALL not send additional open-file messages regardless of trigger source (menu or keyboard)
8. WHILE the UI_Shell is awaiting a response from a previous open-file request, THE UI_Shell SHALL visually disable the "Open..." menu item and ignore the keyboard shortcut without displaying an error
9. IF the user clicks outside File_Menu or presses Escape while File_Menu is expanded, THEN THE Menu_Bar SHALL collapse File_Menu without triggering any action

### Requirement 3: Tab Management

**User Story:** As a user, I want each opened file to appear in its own tab, so that I can switch between multiple files.

#### Acceptance Criteria

1. WHEN a file is selected via the open-file action and a non-empty file path is received, THE UI_Shell SHALL create a new tab with Tab_Header displaying the file name (last path segment, not full path)
2. WHEN a new tab is created, THE UI_Shell SHALL make the new tab the Active_Tab
3. WHEN the user clicks a Tab_Header, THE UI_Shell SHALL make that tab the Active_Tab and display its content in Text_View_Area
4. THE Tab_Container SHALL display Tab_Headers in the order tabs were created (left to right, oldest to newest)
5. WHEN the user clicks the Close_Button on a Tab_Header, THE UI_Shell SHALL remove that tab from Tab_Container
6. IF the closed tab was the Active_Tab AND other tabs remain, THEN THE UI_Shell SHALL make the nearest adjacent tab the Active_Tab (prefer the tab to the right; if none, use the tab to the left)
7. IF the closed tab was the last remaining tab, THEN THE UI_Shell SHALL display the Empty_State_Prompt in Text_View_Area
8. IF the user closes a non-Active_Tab, THEN THE UI_Shell SHALL remove that tab without changing the Active_Tab

### Requirement 4: Tab Header Position

**User Story:** As a user, I want tab headers to be positionable at the top or bottom of the text area, so that I can configure the layout to my preference.

#### Acceptance Criteria

1. THE Tab_Container SHALL support exactly two position values: above Text_View_Area (top) or below Text_View_Area (bottom)
2. WHEN the Application starts, THE Tab_Container SHALL render in the user's last saved position preference; IF no saved preference exists, THEN THE Tab_Container SHALL default to the top position
3. WHEN Tab_Container position changes or any layout-affecting event occurs, THE UI_Shell SHALL re-render the layout preserving all existing tabs, their order, their associated file paths, and the current Active_Tab selection
4. THE UI_Shell SHALL expose a programmatic position property on Tab_Container that accepts the values "top" or "bottom", enabling other components or future settings UI to trigger the position change

### Requirement 5: Empty State

**User Story:** As a user, I want to see a helpful prompt when no file is open, so that I know how to get started.

#### Acceptance Criteria

1. WHEN the Application starts with no files open, THE Text_View_Area SHALL display the text "Ctrl-O to open a file" centered horizontally and vertically within the Text_View_Area bounds
2. WHILE no tabs exist in Tab_Container, THE Text_View_Area SHALL continue to display the Empty_State_Prompt
3. WHEN the last tab is closed, THE Text_View_Area SHALL display the Empty_State_Prompt
4. WHEN a new tab is created from the empty state, THE Text_View_Area SHALL remove the Empty_State_Prompt (file content rendering is handled by a separate feature)

### Requirement 6: Status Bar Display

**User Story:** As a user, I want to see the full path of the currently active file in the status bar plus scan progress feedback, so that I know which file I am viewing and how far along the scan is.

#### Acceptance Criteria

1. WHEN a tab becomes the Active_Tab, THE Status_Bar SHALL display the full absolute file path associated with that tab
2. WHEN no tabs are open, THE Status_Bar SHALL display empty text (no file path)
3. WHEN the Active_Tab changes (via tab click or tab close fallback), THE Status_Bar SHALL update immediately to reflect the new Active_Tab file path
4. WHILE tabs exist in Tab_Container, THE UI_Shell SHALL ensure exactly one tab is the Active_Tab at all times
5. WHILE the active tab scan state is ScanInProgress, THE Status_Bar SHALL display a Progress_Bar element positioned between the file path element and the wrap checkbox, occupying all remaining horizontal flex space
6. WHEN the active tab scan state transitions to a terminal state (ScanComplete, Failed, Cancelled) or no tab is active, THE Status_Bar SHALL hide the Progress_Bar
7. THE Progress_Bar fill width SHALL equal the scan progress percentage (0–100) as reported by the backend get-scroll-info response

### Requirement 7: Open File Dialog Integration

**User Story:** As a user, I want the open-file action to show the native file dialog and create a tab for the selected file, so that the workflow is seamless.

#### Acceptance Criteria

1. WHEN the user presses Ctrl+O (Windows/Linux) or Cmd+O (macOS) and no open-file request is currently awaiting a response, THE UI_Shell SHALL send an "open-file" message to the Backend via Message_Bus_Client and store the returned Correlation_ID as the pending request
2. WHEN the UI_Shell receives a non-empty file path response correlated to the pending open-file request, THE UI_Shell SHALL create a new tab displaying the returned file path as its content
3. WHEN the UI_Shell receives an empty string response (user cancelled dialog), THE UI_Shell SHALL not create a tab and SHALL leave the current display unchanged
4. WHEN the UI_Shell receives a response (empty, non-empty, or error) correlated to the pending open-file request, THE UI_Shell SHALL clear the pending Correlation_ID so that subsequent Ctrl+O/Cmd+O triggers are accepted
5. WHILE an open-file request is awaiting a response (pending Correlation_ID is non-null), IF the user presses Ctrl+O/Cmd+O, THEN THE UI_Shell SHALL suppress the keypress (preventDefault) without sending a duplicate message
6. WHEN the UI_Shell receives an error response correlated to the pending open-file request, THE UI_Shell SHALL display the error message in a modal popup dialog and SHALL clear the pending Correlation_ID to unblock subsequent open-file triggers
