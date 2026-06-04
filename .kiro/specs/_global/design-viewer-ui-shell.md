# Viewer UI Shell — Design

#[[file:.kiro/specs/_global/design-shared.md]]

## Overview

The Viewer UI Shell replaces the initial "Hello World" `AppComponent` with a full tabbed-document interface. The shell provides:

- **Menu_Bar** — drop-down File menu with Open and Exit actions
- **Tab_Container** — horizontal tab strip (position configurable: top/bottom)
- **Text_View_Area** — main content region showing active tab content or empty-state prompt
- **Status_Bar** — displays the full path of the active file

All state lives in Angular signals via `ShellStateService`. The shell integrates with `MessageBusClient` for open-file communication with the .NET backend. Tab position preference persisted to `localStorage` (survives restarts). All other state (tabs, active tab, pending requests) resets on restart.

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

1. **ShellStateService as single source of truth** — All mutable state in one injectable service using signals. Components are pure projections. Avoids prop-drilling; enables property-based testing of service logic without DOM.

2. **AppComponent owns layout only** — CSS Grid arranges children. Reads `tabPosition` to swap Tab_Container placement. Renders error modal overlay when `errorMessage` is non-null.

3. **Keyboard shortcut at document level** — `@HostListener('document:keydown')` on AppComponent. Delegates to ShellStateService. Key comparison uses `event.key.toLowerCase()` (handles CapsLock/Shift).

4. **Tab identity via generated ID** — Each tab gets `crypto.randomUUID()`. Tab order = array insertion order.

5. **No router** — Tabs are not routes. Content switching is signal-driven within Text_View_Area.

6. **localStorage for tab position** — Only persisted preference. Reads on init (defaults `'top'` if absent/invalid), writes on every `setTabPosition()`.

7. **Menu collapse via synchronous DOM manipulation** — Native file dialog (Photino `ShowOpenFile`) blocks webview UI thread. Angular change detection cannot flush before dialog. `onOpen()` directly sets `style.display = 'none'` on dropdown element. Binding re-syncs after dialog closes.

8. **Exit via message bus** — `window.close()` is no-op in Photino webview. Exit sends `'exit'` message → backend calls `app.MainWindow.Close()`.

9. **Race guard sentinel in triggerOpenFile** — `pendingCorrelationId` set to `'__pending__'` before `messageBus.send()`, then updated to real correlationId. Prevents re-entry even if `send()` synchronously triggers callback (defensive — current bus uses microtask dispatch).

## Components and Interfaces

### 1. ShellStateService

Injectable singleton managing all shell state. Owns MessageBusClient subscription lifecycle.

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
  triggerOpenFile(): void;
  closeTab(tabId: string): void;
  activateTab(tabId: string): void;
  setTabPosition(position: TabPosition): void;
  dismissError(): void;
  sendExit(): void;

  // --- Persistence ---
  private loadTabPosition(): TabPosition;
  private persistTabPosition(position: TabPosition): void;
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
  readonly wrapMode = this.state.wrapMode;
  readonly isScanning = this.state.isScanning;
  readonly activeScanProgress = this.state.activeScanProgress;

  onWrapToggle(): void {
    this.state.toggleWrapMode();
  }
}
```

**Template** (`status-bar.component.html`):
```html
<div class="status-bar">
  <span class="file-path">{{ filePath() }}</span>
  @if (isScanning()) {
    <div class="progress-bar">
      <div class="progress-fill" [style.width.%]="activeScanProgress()"></div>
    </div>
  }
  <label class="wrap-checkbox">
    <input type="checkbox" [checked]="wrapMode()" (change)="onWrapToggle()" />
    Wrap
  </label>
</div>
```

**Styles** (`status-bar.component.css`):
```css
.progress-bar {
  flex-grow: 1;
  flex-shrink: 1;
  min-width: 0;
  height: 4px;
  background: #e0e0e0;
  border-radius: 2px;
  margin: 0 8px;
}

.progress-fill {
  height: 100%;
  background: #4a90d9;
  border-radius: 2px;
  transition: width 200ms ease;
}
```

**Signals from ShellStateService:**
- `isScanning` — computed: `activeScanState() === 'ScanInProgress'`
- `activeScanProgress` — computed: active tab's `tabViewState.scanProgress` (0 if no active tab)

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
  const lastSep = Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\'));
  return lastSep === -1 ? filePath : filePath.substring(lastSep + 1);
}
```

### Integration with MessageBusClient

**`triggerOpenFile()`**:
1. Check `pendingCorrelationId() !== null` → early return
2. Set `pendingCorrelationId` to sentinel `'__pending__'`
3. Call `messageBus.send('open-file')` → store returned correlationId
4. Set `pendingCorrelationId` to actual correlationId

**`sendExit()`**:
1. Call `messageBus.send('exit')` — fire-and-forget, backend closes window

**Subscription handler** (registered in constructor):
1. Receive `InboundMessage` with `messageType === 'open-file'`
2. Check `msg.correlationId === this.pendingCorrelationId()` — if not matching, ignore
3. Clear `pendingCorrelationId`
4. If payload starts with `'ERROR:'` → set `errorMessage` signal
5. If `msg.payload === ''` → no-op (user cancelled)
6. If non-empty non-error → create Tab (id via `crypto.randomUUID()`, filePath = payload, fileName via `extractFileName`), append to `tabs`, set as active

**`setTabPosition(position)`**:
1. Set `tabPosition` signal
2. Write to localStorage

**`dismissError()`**:
1. Set `errorMessage` to `null`

## Correctness Properties

### Property 1: State guard prevents duplicate open-file sends

*For any* sequence of `triggerOpenFile` calls and `receiveResponse` events interleaved in any order, `ShellStateService` shall never have more than one outstanding open-file request (pendingCorrelationId transitions: null → correlationId → null, never null → id1 → id2).

**Validates: Requirements 2.7, 7.1, 7.4, 7.5**

### Property 2: File name extraction yields last path segment

*For any* valid file path string containing at least one path separator (forward slash or backslash), `extractFileName` shall return the substring after the last separator. For paths with no separator, it shall return the entire string.

**Validates: Requirements 3.1**

### Property 3: Opening a file creates a tab and makes it active

*For any* non-empty file path received as a response, `ShellStateService` shall append a new tab (increasing length by one), set `filePath` to received path, set `fileName` to last path segment, and set `activeTabId` to new tab's ID.

**Validates: Requirements 3.1, 3.2, 3.4, 7.2**

### Property 4: Empty response preserves tab state

*For any* existing tab state, when an open-file response with empty payload is received, `tabs` array and `activeTabId` shall remain unchanged.

**Validates: Requirements 7.3**

### Property 5: Close tab removes it and selects correct adjacent

*For any* tab array with N ≥ 1 tabs and any tab closed:
- Closed tab removed (length decreases by one)
- If closed was active and tabs remain → new active = right neighbor if exists, else left neighbor
- If closed was not active → `activeTabId` unchanged
- If closed was last tab → `activeTabId` = null

**Validates: Requirements 3.5, 3.6, 3.7, 3.8**

### Property 6: Active file path reflects active tab

*For any* non-empty tab array and valid `activeTabId`, computed `activeFilePath` equals that tab's `filePath`. When `activeTabId` is null or tabs empty, `activeFilePath` = empty string.

**Validates: Requirements 6.1, 6.3**

### Property 7: Exactly one active tab when tabs are non-empty

*For any* sequence of operations (open/close/activate), if `tabs` array is non-empty after operation, then `activeTabId` references exactly one tab present in array.

**Validates: Requirements 6.4**

### Property 8: Position change preserves tab state

*For any* tab state and position change (top↔bottom), `tabs` array contents, order, and `activeTabId` remain identical.

**Validates: Requirements 4.3**

## Error Handling

| Scenario | Strategy |
|----------|----------|
| Open-file error response | Set `errorMessage` → modal; clear `pendingCorrelationId` |
| User dismisses error modal | `dismissError()` → null → modal removed |
| MessageBusClient timeout | Timeout delivered via subscription (empty payload), clears pending |
| Exit via menu | `sendExit()` → backend `app.MainWindow.Close()` |
| Invalid file path chars | Accept as-is — backend validates; frontend displays |
| Tab close on already-removed tab | No-op — checks existence before operating |
| localStorage unavailable | `loadTabPosition` returns `'top'` default; `persistTabPosition` best-effort |

## Testing Strategy

### Property-Based Tests

**Library**: fast-check
**Config**: `{ numRuns: 10 }`
**Test file**: `src/app/shell/shell-state.property.spec.ts`

| Property | Generates | Asserts |
|----------|-----------|---------|
| 1: Guard | Random sequences of `{trigger, responseNonEmpty, responseEmpty}` | At most 1 outstanding; transitions correct |
| 2: File name extraction | Random strings with `/` and `\` separators | Result = substring after last separator |
| 3: Open file creates tab | Random non-empty file paths | Tab appended, fileName correct, activeTabId updated |
| 4: Empty response | Random tab states + empty response | tabs and activeTabId unchanged |
| 5: Close tab | Random tab arrays (1–10) + random close target | Correct removal and adjacency |
| 6: Active file path | Random tab arrays + random activeTabId | Computed matches filePath |
| 7: Active invariant | Random operation sequences | Non-empty tabs → valid activeTabId |
| 8: Position change | Random tab states + toggle | State unchanged |

### Unit Tests

**Test file**: `src/app/shell/shell-state.service.spec.ts` + component `.spec.ts` files

| Test | Validates |
|------|-----------|
| Initial state: tabs empty, activeTabId null, tabPosition from localStorage (default 'top') | Req 4.2, 5.1 |
| triggerOpenFile calls messageBus.send('open-file') | Req 2.4, 2.6 |
| triggerOpenFile while pending does nothing | Req 2.7 |
| Ctrl+O (lowercase/uppercase) triggers triggerOpenFile | Req 2.6 |
| Cmd+O (metaKey) triggers triggerOpenFile | Req 2.6 |
| Other key combos don't trigger | Req 2.6 |
| preventDefault called on Ctrl+O | Req 7.5 |
| Menu "Open..." click triggers triggerOpenFile | Req 2.4 |
| Menu "Exit" click sends 'exit' message | Req 2.5 |
| Menu opens on File click, closes on Escape | Req 2.2, 2.9 |
| File click opens menu (stopPropagation) | Req 2.2, 2.9 |
| Clicking outside closes menu | Req 2.9 |
| Open... disabled when pending | Req 2.8 |
| Empty state prompt shown when no tabs | Req 5.1, 5.2 |
| Empty state hidden when tab exists | Req 5.4 |
| Status bar shows empty when no tabs | Req 6.2 |
| Error response sets errorMessage and clears pending | Req 7.6 |
| Error modal displayed when errorMessage non-null | Req 7.6 |
| dismissError() clears errorMessage | Req 7.6 |
| setTabPosition persists to localStorage | Req 4.2 |
| tabPosition initializes from localStorage | Req 4.2 |
| tabPosition defaults to 'top' when localStorage empty | Req 4.2 |

### Test Boundaries

- **ShellStateService**: mock `MessageBusClient.send()` and `subscribe()`. Simulate responses via subscription callback.
- **Components**: provide ShellStateService with pre-set signal values. Verify template bindings and event handlers.
- **No E2E browser automation** — Photino bridge tested at .NET level.
- **Property tests focus on ShellStateService logic** — pure signal state transitions, no DOM.
