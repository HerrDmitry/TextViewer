# Implementation Plan: Viewer UI Shell

## Overview

Replace the current "Hello World" AppComponent with a full tabbed-document interface shell. The implementation creates a ShellStateService as the single source of truth (signals-based), then builds four child components (MenuBar, TabContainer, TextViewArea, StatusBar) that project state from the service. The AppComponent becomes a CSS Grid layout host with keyboard shortcut handling and error modal overlay.

## Tasks

- [x] 1. Create data models and utility functions
  - [x] 1.1 Create shell data models and extractFileName utility
    - Create `src/app/shell/shell.types.ts` with `TabPosition` type and `Tab` interface
    - Create `src/app/shell/extract-file-name.ts` with the `extractFileName` function handling both `/` and `\` separators
    - _Requirements: 3.1_

  - [x] 1.2 Write property test for file name extraction
    - **Property 2: File name extraction yields last path segment**
    - **Validates: Requirements 3.1**

- [x] 2. Implement ShellStateService
  - [x] 2.1 Create ShellStateService with state signals and computed properties
    - Create `src/app/shell/shell-state.service.ts`
    - Implement all state signals: `tabs`, `activeTabId`, `tabPosition`, `pendingCorrelationId`, `errorMessage`
    - Implement computed signals: `activeTab`, `activeFilePath`, `hasOpenTabs`, `isOpenFilePending`
    - Implement `loadTabPosition` reading from localStorage with try/catch → `'top'` default on any failure (missing, throws, invalid value)
    - Implement `persistTabPosition` writing to localStorage wrapped in try/catch (best-effort, no-op on failure)
    - Implement `setTabPosition` updating signal and persisting
    - Implement `dismissError` clearing errorMessage to null
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 5.1, 6.1, 6.2, 6.3, 6.4_

  - [x] 2.2 Implement tab management actions on ShellStateService
    - Implement `activateTab(tabId)` setting activeTabId
    - Implement `closeTab(tabId)` removing tab, selecting adjacent (prefer right, fallback left), or null if last
    - _Requirements: 3.3, 3.5, 3.6, 3.7, 3.8_

  - [x] 2.3 Implement open-file integration on ShellStateService
    - Implement `triggerOpenFile()` with pending guard, calling `messageBus.send('open-file')` and storing correlationId
    - Implement subscription handler in constructor filtering by `messageType === 'open-file'` AND `correlationId === pendingCorrelationId()` (ignore unrelated messages/correlation IDs)
    - On correlated non-empty non-error payload → create tab (using `extractFileName`), set active
    - On correlated empty payload → no-op (user cancelled)
    - On correlated error payload → set `errorMessage`
    - Clear `pendingCorrelationId` only on correlated response (not on unrelated messages)
    - Implement `ngOnDestroy` to unsubscribe
    - _Requirements: 2.4, 2.6, 2.7, 3.1, 3.2, 3.4, 7.1, 7.2, 7.3, 7.4, 7.5, 7.6_

  - [x] 2.4 Write property test: state guard prevents duplicate open-file sends
    - **Property 1: State guard prevents duplicate open-file sends**
    - **Validates: Requirements 2.7, 7.1, 7.4, 7.5**

  - [x] 2.5 Write property test: opening a file creates a tab and makes it active
    - **Property 3: Opening a file creates a tab and makes it active**
    - **Validates: Requirements 3.1, 3.2, 3.4, 7.2**

  - [x] 2.6 Write property test: empty response preserves tab state
    - **Property 4: Empty response preserves tab state**
    - **Validates: Requirements 7.3**

  - [x] 2.7 Write property test: close tab removes it and selects correct adjacent
    - **Property 5: Close tab removes it and selects correct adjacent**
    - **Validates: Requirements 3.5, 3.6, 3.7, 3.8**

  - [x] 2.8 Write property test: active file path reflects active tab
    - **Property 6: Active file path reflects active tab**
    - **Validates: Requirements 6.1, 6.3**

  - [x] 2.9 Write property test: exactly one active tab when tabs are non-empty
    - **Property 7: Exactly one active tab when tabs are non-empty**
    - **Validates: Requirements 6.4**

  - [x] 2.10 Write property test: position change preserves tab state
    - **Property 8: Position change preserves tab state**
    - **Validates: Requirements 4.3**

- [x] 3. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Implement child components
  - [x] 4.1 Create MenuBarComponent
    - Create `src/app/shell/menu-bar/menu-bar.component.ts` with File menu toggle, Open action (delegates to ShellStateService.triggerOpenFile), Exit action (window.close), disabled state when pending
    - Create `src/app/shell/menu-bar/menu-bar.component.html` with dropdown template, Escape/outside-click to close
    - Create `src/app/shell/menu-bar/menu-bar.component.css` with menu bar styling
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.8, 2.9_

  - [x] 4.2 Create TabContainerComponent
    - Create `src/app/shell/tab-container/tab-container.component.ts` reading tabs and activeTabId from ShellStateService
    - Create `src/app/shell/tab-container/tab-container.component.html` with tab headers, active class, close buttons
    - Create `src/app/shell/tab-container/tab-container.component.css` with tab strip styling
    - _Requirements: 3.3, 3.4, 3.5_

  - [x] 4.3 Create TextViewAreaComponent
    - Create `src/app/shell/text-view-area/text-view-area.component.ts` reading activeTab and hasOpenTabs from ShellStateService
    - Create `src/app/shell/text-view-area/text-view-area.component.html` with conditional empty state prompt or file content
    - Create `src/app/shell/text-view-area/text-view-area.component.css` with content area styling and centered empty state
    - _Requirements: 5.1, 5.2, 5.3, 5.4_

  - [x] 4.4 Create StatusBarComponent
    - Create `src/app/shell/status-bar/status-bar.component.ts` reading activeFilePath from ShellStateService
    - Create `src/app/shell/status-bar/status-bar.component.html` displaying file path
    - Create `src/app/shell/status-bar/status-bar.component.css` with status bar styling
    - _Requirements: 6.1, 6.2, 6.3_

- [x] 5. Wire shell layout in AppComponent
  - [x] 5.1 Rewrite AppComponent as shell layout host
    - Replace current AppComponent implementation with CSS Grid shell layout
    - Import and declare all child components (MenuBarComponent, TabContainerComponent, TextViewAreaComponent, StatusBarComponent)
    - Implement `@HostListener('document:keydown')` for Ctrl+O / Cmd+O delegating to ShellStateService.triggerOpenFile
    - Use `event.key.toLowerCase()` to handle both 'o' and 'O'
    - Add error modal overlay template with dismiss functionality
    - Update `app.component.html` with grid layout, `tabs-bottom` class binding, and error overlay
    - Update or create `app.component.css` with CSS Grid rules for both top and bottom tab positions
    - Update `src/styles.css` (global styles) to set `html, body { margin: 0; padding: 0; overflow: hidden; height: 100%; width: 100%; }` preventing viewport-level scrollbars (Req 1.3)
    - _Requirements: 1.1, 1.2, 1.3, 2.6, 2.7, 7.5, 7.6_

  - [x] 5.2 Write unit tests for ShellStateService
    - Create `src/app/shell/shell-state.service.spec.ts`
    - Test initial state, triggerOpenFile, pending guard, tab creation, tab close adjacency, setTabPosition persistence, dismissError, error response handling
    - Mock MessageBusClient.send() and subscribe()
    - _Requirements: 2.4, 2.6, 2.7, 2.8, 3.1, 3.2, 3.5, 3.6, 3.7, 3.8, 4.2, 5.1, 6.1, 6.2, 7.1, 7.2, 7.3, 7.4, 7.5, 7.6_

  - [x] 5.3 Write unit tests for AppComponent keyboard handling
    - Create or update `src/app/app.component.spec.ts`
    - Test Ctrl+O, Cmd+O, case-insensitive key matching, preventDefault, non-matching keys ignored
    - Test error modal display and dismiss
    - _Requirements: 2.6, 7.5, 7.6_

- [x] 6. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- All property-based tests use `{ numRuns: 10 }` per steering rule
- The shell replaces the existing AppComponent "Hello World" implementation
- ShellStateService is the single source of truth — components are pure projections of signal state

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["1.2", "2.1"] },
    { "id": 2, "tasks": ["2.2", "2.3"] },
    { "id": 3, "tasks": ["2.4", "2.5", "2.6", "2.7", "2.8", "2.9", "2.10", "4.1", "4.2", "4.3", "4.4"] },
    { "id": 4, "tasks": ["5.1"] },
    { "id": 5, "tasks": ["5.2", "5.3"] }
  ]
}
```
