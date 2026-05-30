# Design Document: Viewer UI Shell

#[[file:.kiro/specs/_global/design-shared.md]]

## Overview

The Viewer UI Shell replaces the current "Hello World" `AppComponent` with a full tabbed-document interface. The shell provides:

- **Menu_Bar** — drop-down File menu with Open and Exit actions
- **Tab_Container** — horizontal tab strip (position configurable: top/bottom)
- **Text_View_Area** — main content region showing active tab content or empty-state prompt
- **Status_Bar** — displays the full path of the active file

All state lives in Angular signals. The shell integrates with the existing `MessageBusClient` service for open-file communication with the .NET backend. Tab position preference is persisted to `localStorage` so it survives restarts (see ShellStateService). All other state (tabs, active tab, pending requests) resets on restart.

## Architecture

```mermaid
graph TD
    subgraph "Angular Application"
        A[AppComponent - Shell Host] --> B[MenuBarComponent]
        A --> C[TabContainerComponent]
        A --> D[TextViewAreaComponent]
        A --> E[StatusBarComponent]
        A --> F[ShellStateService]
        F --> G[MessageBusClient]
    end

    subgraph "State (Signals)"
        F --> H[tabs: Signal&lt;Tab[]&gt;]
        F --> I[activeTabId: Signal&lt;string|null&gt;]
        F --> J[tabPosition: Signal&lt;top|bottom&gt;]
        F --> K[pendingCorrelationId: Signal&lt;string|null&gt;]
        F --> L[errorMessage: Signal&lt;string|null&gt;]
    end
```

### Component Hierarchy

```
AppComponent (shell layout host)
├── MenuBarComponent (File menu, keyboard shortcut handling)
├── TabContainerComponent (tab headers, close buttons)
├── TextViewAreaComponent (file content / empty state)
└── StatusBarComponent (active file path)
```

### Design Decisions

1. **ShellStateService as single source of truth** — All mutable state lives in one injectable service using signals. Components are pure projections of that state. This avoids prop-drilling and makes property-based testing straightforward (test the service, not the DOM).

2. **AppComponent owns layout only** — The shell host uses CSS Grid to arrange children. It reads `tabPosition` to swap Tab_Container placement relative to Text_View_Area. It also renders the error modal overlay when `errorMessage` is non-null.

3. **Keyboard shortcut at document level** — `@HostListener('document:keydown')` stays on AppComponent (same pattern as current implementation). It delegates to ShellStateService for the open-file action. Key comparison uses `event.key.toLowerCase()` to handle both 'o' and 'O' (CapsLock or Shift state).

4. **Tab identity via generated ID** — Each tab gets a unique ID (crypto.randomUUID). Tab order is array insertion order.

5. **No router** — Tabs are not routes. Content switching is signal-driven within Text_View_Area.

6. **localStorage for tab position** — Tab position is the only persisted preference. `ShellStateService` reads from `localStorage('tabPosition')` on init (defaults to `'top'` if absent or invalid) and writes on every `setTabPosition()` call. This satisfies Req 4.2 ("last saved position preference") with minimal persistence scope.

7. **Menu collapse via synchronous DOM manipulation** — The native file dialog (Photino `ShowOpenFile`) blocks the webview UI thread. Angular change detection cannot flush before the dialog appears. To guarantee the dropdown is visually hidden before the dialog opens, `onOpen()` directly sets `style.display = 'none'` on the dropdown element. The `[style.display]` binding (always-in-DOM pattern) re-syncs state after the dialog closes.

8. **Exit via message bus** — `window.close()` is a no-op in Photino's webview context. Exit is implemented by sending an `'exit'` message to the backend, where the handler calls `app.MainWindow.Close()`.

9. **Race guard sentinel in triggerOpenFile** — `pendingCorrelationId` is set to `'__pending__'` before calling `messageBus.send()`, then updated to the real correlationId after. This prevents re-entry even if `send()` were to synchronously trigger a response callback (defensive hardening — current bus uses microtask dispatch so the race cannot occur in practice).

## Components and Interfaces

### 1. ShellStateService

Injectable singleton managing all shell state. Owns the MessageBusClient subscription lifecycle.

```typescript
@Injectable({ providedIn: 'root' })
export class ShellStateService implements OnDestroy {
  private static readonly TAB_POSITION_KEY = 'tabPosition';

  // --- State signals ---
  readonly tabs = signal<Tab[]>([]);
  readonly activeTabId = signal<string | null>(null);
  readonly tabPosition = signal<TabPosition>(this.loadTabPosition());
  readonly pendingCorrelationId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);

  // --- Computed signals ---
  readonly activeTab = computed(() => {
    const id = this.activeTabId();
    return this.tabs().find(t => t.id === id) ?? null;
  });
  readonly activeFilePath = computed(() => this.activeTab()?.filePath ?? '');
  readonly hasOpenTabs = computed(() => this.tabs().length > 0);
  readonly isOpenFilePending = computed(() => this.pendingCorrelationId() !== null);

  private readonly messageBus = inject(MessageBusClient);
  private subscription: SubscriptionHandle;

  constructor() { /* subscribe to 'open-file' responses */ }
  ngOnDestroy(): void { /* unsubscribe */ }

  // --- Actions ---
  triggerOpenFile(): void;       // sets sentinel, sends open-file if not pending
  closeTab(tabId: string): void; // removes tab, adjusts activeTabId
  activateTab(tabId: string): void; // sets activeTabId
  setTabPosition(position: TabPosition): void; // updates signal + persists to localStorage
  dismissError(): void;          // clears errorMessage to null
  sendExit(): void;              // sends 'exit' message to backend → window closes

  // --- Persistence ---
  private loadTabPosition(): TabPosition {
    const stored = localStorage.getItem(ShellStateService.TAB_POSITION_KEY);
    return stored === 'bottom' ? 'bottom' : 'top'; // default 'top' if absent or invalid
  }

  private persistTabPosition(position: TabPosition): void {
    localStorage.setItem(ShellStateService.TAB_POSITION_KEY, position);
  }
}
```

### 2. AppComponent (Shell Host)

```typescript
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [MenuBarComponent, TabContainerComponent, TextViewAreaComponent, StatusBarComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent {
  private readonly state = inject(ShellStateService);
  readonly tabPosition = this.state.tabPosition;
  readonly errorMessage = this.state.errorMessage;

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    const isCtrlO = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'o';
    if (!isCtrlO) return;
    event.preventDefault();
    this.state.triggerOpenFile();
  }

  dismissError(): void {
    this.state.dismissError();
  }
}
```

**Template** (`app.component.html`):
```html
<div class="shell" [class.tabs-bottom]="tabPosition() === 'bottom'">
  <app-menu-bar />
  <app-tab-container />
  <app-text-view-area />
  <app-status-bar />
</div>

@if (errorMessage()) {
  <div class="error-overlay" (click)="dismissError()">
    <div class="error-modal" role="alertdialog" aria-modal="true" (click)="$event.stopPropagation()">
      <p class="error-text">{{ errorMessage() }}</p>
      <button class="error-dismiss" (click)="dismissError()">OK</button>
    </div>
  </div>
}
```

**Layout** (`app.component.css`):
```css
.shell {
  display: grid;
  grid-template-rows: auto auto 1fr auto;
  grid-template-areas:
    "menu"
    "tabs"
    "content"
    "status";
  height: 100vh;
  width: 100vw;
  margin: 0;
  overflow: hidden;
}

.shell.tabs-bottom {
  grid-template-rows: auto 1fr auto auto;
  grid-template-areas:
    "menu"
    "content"
    "tabs"
    "status";
}

app-menu-bar    { grid-area: menu; }
app-tab-container { grid-area: tabs; }
app-text-view-area { grid-area: content; overflow: hidden; }
app-status-bar  { grid-area: status; }
```

### 3. MenuBarComponent

```typescript
@Component({
  selector: 'app-menu-bar',
  standalone: true,
  templateUrl: './menu-bar.component.html',
  styleUrl: './menu-bar.component.css'
})
export class MenuBarComponent {
  private readonly state = inject(ShellStateService);
  private readonly el = inject(ElementRef);
  readonly isOpenDisabled = this.state.isOpenFilePending;
  menuOpen = signal(false);

  toggleMenu(): void { this.menuOpen.update(v => !v); }
  closeMenu(): void { this.menuOpen.set(false); }

  onOpen(): void {
    this.menuOpen.set(false);
    // Synchronous DOM hide — ensures dropdown gone before native dialog blocks UI thread
    const dropdown = this.el.nativeElement.querySelector('.dropdown') as HTMLElement | null;
    if (dropdown) dropdown.style.display = 'none';
    this.state.triggerOpenFile();
  }

  onExit(): void {
    this.state.sendExit();
  }
}
```

**Template** (`menu-bar.component.html`):
```html
<nav class="menu-bar" (document:keydown.escape)="closeMenu()" (document:click)="closeMenu()">
  <div class="menu-item" (click)="toggleMenu(); $event.stopPropagation()">
    <span class="menu-label">File</span>
    <ul class="dropdown" [style.display]="menuOpen() ? 'block' : 'none'" (click)="$event.stopPropagation()">
      <li [class.disabled]="isOpenDisabled()" (click)="!isOpenDisabled() && onOpen()">Open...</li>
      <li (click)="onExit()">Exit</li>
    </ul>
  </div>
</nav>
```

### 4. TabContainerComponent

```typescript
@Component({
  selector: 'app-tab-container',
  standalone: true,
  templateUrl: './tab-container.component.html',
  styleUrl: './tab-container.component.css'
})
export class TabContainerComponent {
  private readonly state = inject(ShellStateService);
  readonly tabs = this.state.tabs;
  readonly activeTabId = this.state.activeTabId;

  onTabClick(tabId: string): void {
    this.state.activateTab(tabId);
  }

  onCloseTab(tabId: string, event: MouseEvent): void {
    event.stopPropagation();
    this.state.closeTab(tabId);
  }
}
```

**Template** (`tab-container.component.html`):
```html
<div class="tab-strip">
  @for (tab of tabs(); track tab.id) {
    <div class="tab-header"
         [class.active]="tab.id === activeTabId()"
         (click)="onTabClick(tab.id)">
      <span class="tab-label">{{ tab.fileName }}</span>
      <button class="close-btn" (click)="onCloseTab(tab.id, $event)" aria-label="Close tab">×</button>
    </div>
  }
</div>
```

### 5. TextViewAreaComponent

```typescript
@Component({
  selector: 'app-text-view-area',
  standalone: true,
  templateUrl: './text-view-area.component.html',
  styleUrl: './text-view-area.component.css'
})
export class TextViewAreaComponent {
  private readonly state = inject(ShellStateService);
  readonly activeTab = this.state.activeTab;
  readonly hasOpenTabs = this.state.hasOpenTabs;
}
```

**Template** (`text-view-area.component.html`):
```html
<div class="text-view-area">
  @if (!hasOpenTabs()) {
    <div class="empty-state">Ctrl-O to open a file</div>
  }
</div>
```

### 6. StatusBarComponent

```typescript
@Component({
  selector: 'app-status-bar',
  standalone: true,
  templateUrl: './status-bar.component.html',
  styleUrl: './status-bar.component.css'
})
export class StatusBarComponent {
  private readonly state = inject(ShellStateService);
  readonly filePath = this.state.activeFilePath;
}
```

**Template** (`status-bar.component.html`):
```html
<div class="status-bar">
  <span class="file-path">{{ filePath() }}</span>
</div>
```

## Data Models

```typescript
/** Position of the tab container relative to the text view area */
export type TabPosition = 'top' | 'bottom';

/** Represents a single open file tab */
export interface Tab {
  /** Unique identifier (crypto.randomUUID) */
  id: string;
  /** Full absolute file path (from backend response) */
  filePath: string;
  /** Display name — last segment of filePath */
  fileName: string;
}
```

### State Shape (ShellStateService signals)

| Signal | Type | Initial Value | Description |
|--------|------|---------------|-------------|
| `tabs` | `Tab[]` | `[]` | Ordered list of open tabs |
| `activeTabId` | `string \| null` | `null` | ID of the currently active tab |
| `tabPosition` | `TabPosition` | `localStorage('tabPosition')` or `'top'` | Tab strip position (persisted) |
| `pendingCorrelationId` | `string \| null` | `null` | Guards duplicate open-file requests |
| `errorMessage` | `string \| null` | `null` | Error text for modal display; null = no error |

### Derived State (computed signals)

| Computed | Type | Derivation |
|----------|------|------------|
| `activeTab` | `Tab \| null` | `tabs().find(t => t.id === activeTabId())` |
| `activeFilePath` | `string` | `activeTab()?.filePath ?? ''` |
| `hasOpenTabs` | `boolean` | `tabs().length > 0` |
| `isOpenFilePending` | `boolean` | `pendingCorrelationId() !== null` |

### File Name Extraction

```typescript
function extractFileName(filePath: string): string {
  // Handle both Windows backslash and Unix forward slash
  const lastSep = Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\'));
  return lastSep === -1 ? filePath : filePath.substring(lastSep + 1);
}
```

### Integration with MessageBusClient

The `ShellStateService.triggerOpenFile()` method:
1. Checks `pendingCorrelationId() !== null` → early return (guard)
2. Sets `pendingCorrelationId` to sentinel `'__pending__'` (prevents re-entry if `send()` triggers synchronous callback)
3. Calls `messageBus.send('open-file')` → stores returned correlationId
4. Sets `pendingCorrelationId` signal to actual correlationId

The `ShellStateService.sendExit()` method:
1. Calls `messageBus.send('exit')` — fire-and-forget, backend closes window

The subscription handler (registered in constructor):
1. Receives `InboundMessage` with `messageType === 'open-file'`
2. Checks `msg.correlationId === this.pendingCorrelationId()` — if not matching, ignore
3. Clears `pendingCorrelationId` (on any correlated response)
4. If payload starts with `ERROR_PREFIX` (`'ERROR:'`) → sets `errorMessage` signal with the error text
5. If `msg.payload === ''` → no-op (user cancelled)
6. If non-empty non-error payload → creates new Tab (id via crypto.randomUUID(), filePath = payload, fileName via extractFileName), appends to `tabs`, sets as active

The `setTabPosition(position)` method:
1. Sets `tabPosition` signal to the new value
2. Calls `persistTabPosition(position)` to write to localStorage

The `dismissError()` method:
1. Sets `errorMessage` signal to `null`

## Correctness Properties

*A property is a characteristic or behavior that should hold true across all valid executions of a system — essentially, a formal statement about what the system should do. Properties serve as the bridge between human-readable specifications and machine-verifiable correctness guarantees.*

### Property 1: State guard prevents duplicate open-file sends

*For any* sequence of `triggerOpenFile` calls and `receiveResponse` events interleaved in any order, the `ShellStateService` shall never have more than one outstanding open-file request (pendingCorrelationId transitions: null → correlationId → null, never null → id1 → id2).

**Validates: Requirements 2.7, 7.1, 7.4, 7.5**

### Property 2: File name extraction yields last path segment

*For any* valid file path string containing at least one path separator (forward slash or backslash), `extractFileName` shall return the substring after the last separator. For paths with no separator, it shall return the entire string.

**Validates: Requirements 3.1**

### Property 3: Opening a file creates a tab and makes it active

*For any* non-empty file path received as a response, the `ShellStateService` shall append a new tab to the `tabs` array (increasing length by one), set that tab's `filePath` to the received path, set its `fileName` to the last path segment, and set `activeTabId` to the new tab's ID.

**Validates: Requirements 3.1, 3.2, 3.4, 7.2**

### Property 4: Empty response preserves tab state

*For any* existing tab state (including empty), when an open-file response with an empty payload is received, the `tabs` array and `activeTabId` shall remain unchanged.

**Validates: Requirements 7.3**

### Property 5: Close tab removes it and selects correct adjacent

*For any* tab array with N ≥ 1 tabs and any tab closed:
- The closed tab shall no longer appear in the `tabs` array (length decreases by one).
- If the closed tab was the active tab and tabs remain, the new active tab shall be the right neighbor (index + 1) if it exists, otherwise the left neighbor (index - 1).
- If the closed tab was not the active tab, `activeTabId` shall remain unchanged.
- If the closed tab was the last tab, `activeTabId` shall become null.

**Validates: Requirements 3.5, 3.6, 3.7, 3.8**

### Property 6: Active file path reflects active tab

*For any* non-empty tab array and any valid `activeTabId` pointing to a tab in that array, the computed `activeFilePath` shall equal that tab's `filePath`. When `activeTabId` is null or tabs is empty, `activeFilePath` shall be the empty string.

**Validates: Requirements 6.1, 6.3**

### Property 7: Exactly one active tab when tabs are non-empty

*For any* sequence of operations (open file, close tab, activate tab) applied to the `ShellStateService`, if the `tabs` array is non-empty after the operation, then `activeTabId` shall reference exactly one tab present in the `tabs` array.

**Validates: Requirements 6.4**

### Property 8: Position change preserves tab state

*For any* tab state (tabs array, activeTabId) and any position change (top→bottom or bottom→top), the `tabs` array contents, their order, and `activeTabId` shall remain identical after the position change.

**Validates: Requirements 4.3**

## Error Handling

| Scenario | Strategy |
|----------|----------|
| Open-file response is error (non-empty error payload) | Set `errorMessage` signal with error text → AppComponent renders modal dialog; clear `pendingCorrelationId` to unblock |
| User dismisses error modal | `dismissError()` sets `errorMessage` to null → modal removed from DOM |
| MessageBusClient timeout on open-file | Timeout notification delivered via subscription (empty payload), clears pending state |
| Window exit via "Exit" menu | `sendExit()` sends `'exit'` message → backend handler calls `app.MainWindow.Close()` |
| Invalid file path characters in response | Accept as-is — backend validates; frontend displays what it receives |
| Tab close on already-removed tab (race) | No-op — `closeTab` checks tab exists in array before operating |
| localStorage unavailable or throws | `loadTabPosition` catches and returns `'top'` default; `persistTabPosition` is best-effort (no-op on failure) |

## Testing Strategy

### Property-Based Tests

**Library**: fast-check (already in devDependencies)
**Config**: `{ numRuns: 10 }` per steering rule
**Test file**: `src/app/shell-state.property.spec.ts`

| Property | Generates | Asserts |
|----------|-----------|---------|
| Property 1: Guard | Random sequences of `{trigger, responseNonEmpty, responseEmpty}` events | At most 1 outstanding send; pendingCorrelationId transitions correctly |
| Property 2: File name extraction | Random strings with embedded `/` and `\` separators | Result equals substring after last separator |
| Property 3: Open file creates tab | Random non-empty file paths | Tab appended, fileName correct, activeTabId updated |
| Property 4: Empty response | Random existing tab states + empty response | tabs and activeTabId unchanged |
| Property 5: Close tab | Random tab arrays (1–10 tabs) + random tab to close | Correct removal and adjacency selection |
| Property 6: Active file path | Random tab arrays + random activeTabId | Computed value matches tab's filePath |
| Property 7: Active invariant | Random operation sequences (open/close/activate) | Non-empty tabs → activeTabId valid |
| Property 8: Position change | Random tab states + position toggle | State unchanged |

### Unit Tests

**Test file**: `src/app/shell-state.spec.ts` (service), component-specific `.spec.ts` files

| Test | Validates |
|------|-----------|
| Initial state: tabs empty, activeTabId null, tabPosition from localStorage (default 'top') | Req 4.2, 5.1 |
| triggerOpenFile calls messageBus.send('open-file') | Req 2.4, 2.6 |
| triggerOpenFile while pending does nothing | Req 2.7 |
| Ctrl+O (lowercase 'o') keydown triggers triggerOpenFile | Req 2.6 |
| Ctrl+O (uppercase 'O') keydown triggers triggerOpenFile | Req 2.6 |
| Cmd+O (metaKey) triggers triggerOpenFile | Req 2.6 |
| Other key combos don't trigger | Req 2.6 |
| preventDefault called on Ctrl+O | Req 7.5 |
| Menu "Open..." click triggers triggerOpenFile | Req 2.4 |
| Menu "Exit" click sends 'exit' message via bus | Req 2.5 |
| Menu opens on File click, closes on Escape | Req 2.2, 2.9 |
| File click opens menu without immediate close from document click handler (stopPropagation) | Req 2.2, 2.9 |
| Escape key closes open menu | Req 2.9 |
| Clicking outside the menu (document click) closes open menu | Req 2.9 |
| Menu closes on outside click | Req 2.9 |
| Open... disabled when pending | Req 2.8 |
| Empty state prompt shown when no tabs | Req 5.1, 5.2 |
| Empty state hidden when tab exists | Req 5.4 |
| Status bar shows empty when no tabs | Req 6.2 |
| Error response sets errorMessage and clears pending | Req 7.6 |
| Error modal displayed when errorMessage is non-null | Req 7.6 |
| dismissError() clears errorMessage to null and hides modal | Req 7.6 |
| setTabPosition persists value to localStorage | Req 4.2 |
| tabPosition initializes from localStorage on construction | Req 4.2 |
| tabPosition defaults to 'top' when localStorage is empty | Req 4.2 |

### Test Boundaries

- **ShellStateService** tests: mock `MessageBusClient.send()` and `subscribe()`. Simulate responses by invoking the subscription callback directly.
- **Component** tests: provide `ShellStateService` with pre-set signal values. Verify template bindings and event handlers.
- **No E2E browser automation** — Photino bridge tested via integration tests at the .NET level.
- **Property tests focus on ShellStateService logic** — pure signal state transitions, no DOM.

