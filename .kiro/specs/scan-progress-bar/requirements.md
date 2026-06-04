# Requirements Document

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

A progress bar in the status bar that visualizes file scan progress. The bar occupies all horizontal space between the file name element and the wrap checkbox, providing real-time feedback during the FileIndex scanning phase.

## Glossary

- **Status_Bar**: The bottom bar of the application shell displaying file path, progress, and wrap toggle
- **Progress_Bar**: A horizontal bar element showing scan completion percentage between file name and wrap checkbox
- **Scan_Progress**: The percentage of file bytes scanned, derived from bytes-read / file-size during scanning
- **FileIndex_Service**: Backend service performing single-pass file scanning (produces ScanInProgress → ScanComplete/Failed/Cancelled)
- **Shell_State_Service**: Angular singleton managing all shell UI state including scan progress signals

## Requirements

### Requirement 1: Progress Bar Visibility

**User Story:** As a user, I want to see a progress bar during file scanning, so that I know the application is working and how far along the scan is.

#### Acceptance Criteria

1. WHILE the active tab scan state is ScanInProgress, THE Status_Bar SHALL display the Progress_Bar element positioned between the file path element and the wrap checkbox
2. WHEN the active tab scan state transitions to ScanComplete, THE Status_Bar SHALL hide the Progress_Bar
3. WHEN the active tab scan state transitions to Failed or Cancelled, THE Status_Bar SHALL hide the Progress_Bar
4. WHEN no tab is active (activeTabId is null), THE Status_Bar SHALL hide the Progress_Bar immediately, even if background scans are still running
5. [Removed — covered by Requirement 5, AC 5.1]

### Requirement 2: Progress Bar Layout

**User Story:** As a user, I want the progress bar to fill the available space between existing elements, so that it is clearly visible without disrupting the status bar layout.

#### Acceptance Criteria

1. THE Progress_Bar SHALL occupy all horizontal space between the file name element and the wrap checkbox
2. THE Progress_Bar SHALL use CSS flex-grow to fill remaining space in the status bar flex container with a flex-shrink value that allows it to shrink to a minimum width of 0 when space is constrained
3. THE Progress_Bar SHALL have left and right margins of approximately 8px (±1px tolerance for browser rounding) separating it from adjacent elements
4. THE Progress_Bar SHALL have a fixed height no greater than 16px so that the status bar's computed outer height remains unchanged from its current dimensions
5. THE Progress_Bar SHALL appear in the DOM order after the file name element and before the wrap checkbox within the status bar flex container

### Requirement 3: Progress Percentage Display

**User Story:** As a user, I want to see the scan completion percentage, so that I can estimate remaining time.

#### Acceptance Criteria

1. WHILE scan state is ScanInProgress, THE Progress_Bar SHALL display a fill element whose inline width style equals the scan progress integer value (0–100) expressed as a percentage of the Progress_Bar total width
2. WHEN a get-scroll-info poll response is received with ScanInProgress state, THE Shell_State_Service SHALL parse the progress percentage field from the response payload and store it as an integer (0–100) in the corresponding session's scan progress signal
3. IF no get-scroll-info poll response has been received yet for a session, THEN THE Shell_State_Service SHALL default the scan progress value to 0

### Requirement 4: Progress Data Source

**User Story:** As a developer, I want scan progress reported from the backend, so that the frontend can display accurate completion percentage.

#### Acceptance Criteria

1. WHILE a scan is in progress, THE FileIndex SHALL track the number of bytes read from the file stream and the total file size (obtained from the stream length at scan start), where bytes_read is incremented by the count returned from each ReadAsync call and total_file_size is the stream length in bytes captured before the scan loop begins
2. WHEN a get-scroll-info request is received for a session in ScanInProgress state, THE backend SHALL include the progress percentage as a fifth newline-delimited field in the response payload, formatted as `{scanState}\n{lineCount}\n{maxByteLength}\n{maxCharLength}\n{progressPercentage}`
3. THE progress percentage SHALL be computed as floor(bytes_read / total_file_size * 100), yielding an integer value in the range 0 to 100 inclusive
4. IF total file size is zero, THEN THE backend SHALL report progress as 100
5. WHEN a get-scroll-info request is received for a session in a terminal state (ScanComplete, Failed, or Cancelled), THE backend SHALL report progress as 100
6. THE bytes_read property SHALL be safe to read from any thread at any time without synchronization by using a volatile or interlocked mechanism, ensuring concurrent get-scroll-info reads never observe a torn value

### Requirement 5: Tab Switching Behavior

**User Story:** As a user, I want the progress bar to reflect the active tab's scan state when switching tabs, so that I always see the correct status.

#### Acceptance Criteria

1. WHEN the user switches to a tab with ScanInProgress state, THE Status_Bar SHALL display the Progress_Bar with that tab's last-known progress percentage without clamping (value may exceed 100 if backend reports it)
2. WHEN the user switches to a tab with ScanComplete state, THE Status_Bar SHALL hide the Progress_Bar
3. WHEN the user switches to a tab with Failed or Cancelled state, THE Status_Bar SHALL hide the Progress_Bar
4. WHEN the user switches to a tab with NotStarted state, THE Status_Bar SHALL hide the Progress_Bar
5. WHEN the user switches to a tab with ScanInProgress state, THE Shell_State_Service SHALL resume scrollbar polling for that tab's session so progress updates continue arriving

### Requirement 6: Progress Bar Appearance

**User Story:** As a user, I want the progress bar to be visually clear and unobtrusive, so that it communicates status without distracting from file content.

#### Acceptance Criteria

1. THE Progress_Bar SHALL have a light gray background track (#e0e0e0)
2. THE Progress_Bar fill SHALL use a blue color (#4a90d9) to indicate progress
3. THE Progress_Bar SHALL have a height of approximately 4px (minor variations acceptable due to browser rendering)
4. THE Progress_Bar track and fill SHALL both have rounded corners (border-radius: 2px)
5. THE Progress_Bar fill transition SHALL animate smoothly using CSS transition on width (200ms ease)
