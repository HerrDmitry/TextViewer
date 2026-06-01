import { computed, Injectable, inject, OnDestroy, signal } from '@angular/core';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage, SubscriptionHandle } from '../services/message-bus.types';
import { extractFileName } from './extract-file-name';
import { splitIntoVisualRows, computeGutterWidth } from './line-wrap-utils';
import { Tab, TabPosition, TabViewState, ScrollbarState, ScanStateValue, ViewDimensions, DragState } from './shell.types';

/** Number of lines/columns to scroll per mouse wheel tick */
export const WHEEL_STEP = 3;
/** Number of lines/columns to scroll per arrow key press */
export const ARROW_STEP = 1;
/** Minimum thumb size in pixels */
export const MIN_THUMB_SIZE = 20;

/** Clamp a value between min and max (inclusive) */
export function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

/**
 * Compute horizontal scrollbar max based on scan state.
 * QuickScanInProgress / QuickScanComplete → maxByteLength
 * FullScanInProgress / FullScanComplete → maxCharLength
 * Default (NotStarted, Failed, Cancelled) → 0
 */
export function computeHorizontalMax(
  scanState: ScanStateValue,
  maxByteLength: number,
  maxCharLength: number
): number {
  switch (scanState) {
    case 'QuickScanInProgress':
    case 'QuickScanComplete':
      return maxByteLength;
    case 'FullScanInProgress':
    case 'FullScanComplete':
      return maxCharLength;
    default:
      return 0;
  }
}

/**
 * ShellStateService — single source of truth for all shell UI state.
 * Owns signals for tabs, active tab, tab position, pending requests, and errors.
 * Integrates with MessageBusClient for open-file communication.
 */
@Injectable({ providedIn: 'root' })
export class ShellStateService implements OnDestroy {
  private static readonly TAB_POSITION_KEY = 'tabPosition';

  // --- State signals ---
  readonly tabs = signal<Tab[]>([]);
  readonly activeTabId = signal<string | null>(null);
  readonly tabPosition = signal<TabPosition>(this.loadTabPosition());
  readonly pendingCorrelationId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly tabViewStates = signal<Map<string, TabViewState>>(new Map());
  readonly viewDimensions = signal<ViewDimensions | null>(null);
  readonly dragState = signal<DragState | null>(null);
  readonly wrapMode = signal<boolean>(false);

  readonly charMetricsWidth = signal<number>(0);

  // --- Computed signals ---
  readonly activeTab = computed(() => {
    const id = this.activeTabId();
    return this.tabs().find(t => t.id === id) ?? null;
  });
  readonly activeFilePath = computed(() => this.activeTab()?.filePath ?? '');
  readonly hasOpenTabs = computed(() => this.tabs().length > 0);
  readonly isOpenFilePending = computed(() => this.pendingCorrelationId() !== null);
  readonly activeViewRows = computed(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    const state = this.tabViewStates().get(tab.viewSessionId);
    return state?.viewRows ?? null;
  });
  readonly activeViewError = computed(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    const state = this.tabViewStates().get(tab.viewSessionId);
    return state?.errorMessage ?? null;
  });
  readonly isViewPending = computed(() => {
    const tab = this.activeTab();
    if (!tab) return false;
    const state = this.tabViewStates().get(tab.viewSessionId);
    return state?.pendingCorrelationId !== null;
  });
  readonly activeScrollbarState = computed(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    const state = this.tabViewStates().get(tab.viewSessionId);
    return state?.scrollbarState ?? null;
  });

  readonly activeTabViewState = computed(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    return this.tabViewStates().get(tab.viewSessionId) ?? null;
  });

  readonly activeTotalLogicalLines = computed<number>(() => {
    // Use scrollbar verticalMax directly — in wrapped mode this is visual row count
    // from backend get-wrapped-line-count; in non-wrapped mode it's logical line count.
    const sb = this.activeScrollbarState();
    if (!sb) return 0;
    return sb.verticalMax;
  });

  readonly activeGutterWidth = computed(() => {
    const totalLines = this.activeTotalLogicalLines();
    const charWidth = this.charMetricsWidth();
    return computeGutterWidth(totalLines, charWidth);
  });

  readonly activeGutterNumbers = computed<(number | null)[]>(() => {
    const state = this.activeTabViewState();
    if (!state) return [];
    return state.gutterNumbers ?? [];
  });

  readonly verticalThumbRatio = computed(() => {
    const sb = this.activeScrollbarState();
    const dims = this.viewDimensions();
    if (!sb || sb.disabled || !dims) return 1;
    if (sb.verticalMax <= dims.rowCount) return 1;
    return dims.rowCount / sb.verticalMax;
  });

  readonly horizontalThumbRatio = computed(() => {
    const sb = this.activeScrollbarState();
    const dims = this.viewDimensions();
    if (!sb || sb.disabled || !dims) return 1;
    if (sb.horizontalMax <= dims.colCount) return 1;
    return dims.colCount / sb.horizontalMax;
  });

  readonly verticalThumbFraction = computed(() => {
    const tab = this.activeTab();
    if (!tab) return 0;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return 0;
    const sb = state.scrollbarState;
    const dims = this.viewDimensions();
    if (!dims || sb.disabled || sb.verticalMax <= dims.rowCount) return 0;
    const maxScroll = sb.verticalMax - dims.rowCount;
    if (maxScroll <= 0) return 0;

    if (this.wrapMode()) {
      // In wrapped mode, use startLine and characterOffset with verticalMax from backend
      // Backend resolves visual row position; approximate fraction from startLine ratio
      const dims = this.viewDimensions();
      if (!dims) return 0;
      // Use startLine as visual row index (backend-resolved position tracking)
      return state.startLine / maxScroll;
    }

    return state.startLine / maxScroll;
  });

  readonly horizontalThumbFraction = computed(() => {
    const tab = this.activeTab();
    if (!tab) return 0;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return 0;
    const sb = state.scrollbarState;
    const dims = this.viewDimensions();
    if (!dims || sb.disabled || sb.horizontalMax <= dims.colCount) return 0;
    const maxScroll = sb.horizontalMax - dims.colCount;
    if (maxScroll <= 0) return 0;
    return state.startCol / maxScroll;
  });

  // --- Dependencies ---
  private readonly messageBus = inject(MessageBusClient);
  private subscription: SubscriptionHandle | undefined;
  private scanCompleteSubscription: SubscriptionHandle | undefined;
  private getViewSubscription: SubscriptionHandle | undefined;
  private scrollInfoSubscription: SubscriptionHandle | undefined;
  private wrappedLineCountSubscription: SubscriptionHandle | undefined;

  // --- Scrollbar polling state ---
  private scrollPollTimer: ReturnType<typeof setInterval> | null = null;
  private scrollPollSessionId: string | null = null;

  private static readonly ERROR_PREFIX = 'ERROR:';

  constructor() {
    this.subscription = this.messageBus.subscribe('open-file', (msg: InboundMessage) => {
      // Only process messages correlated to our pending request
      if (msg.correlationId !== this.pendingCorrelationId()) return;

      // Clear pending state on any correlated response
      this.pendingCorrelationId.set(null);

      // Error response
      if (msg.payload.startsWith(ShellStateService.ERROR_PREFIX)) {
        this.errorMessage.set(msg.payload);
        return;
      }

      // Empty payload — user cancelled, no-op
      if (msg.payload === '') return;

      // Non-empty, non-error payload — parse viewSessionId\nfilePath\nrow1\nrow2\n... format
      const firstNewline = msg.payload.indexOf('\n');
      let viewSessionId: string;
      let filePath: string;
      let initialRows: string[] | null = null;
      if (firstNewline === -1) {
        // Backward compat: no newline means entire payload is filePath
        viewSessionId = crypto.randomUUID();
        filePath = msg.payload;
      } else {
        viewSessionId = msg.payload.substring(0, firstNewline);
        const afterFirst = msg.payload.substring(firstNewline + 1);
        const secondNewline = afterFirst.indexOf('\n');
        if (secondNewline === -1) {
          // Only viewSessionId\nfilePath — no initial rows
          filePath = afterFirst;
        } else {
          filePath = afterFirst.substring(0, secondNewline);
          const rowData = afterFirst.substring(secondNewline + 1);
          initialRows = rowData.length > 0 ? rowData.split('\n') : null;
        }
      }

      const newTab: Tab = {
        id: crypto.randomUUID(),
        filePath,
        fileName: extractFileName(filePath),
        viewSessionId,
      };
      this.tabs.update(tabs => [...tabs, newTab]);
      this.activeTabId.set(newTab.id);

      // Create initial TabViewState entry for this session with Initial_View rows
      const currentStates = this.tabViewStates();
      const updatedStates = new Map(currentStates);
      updatedStates.set(viewSessionId, {
        scanComplete: false,
        viewRows: initialRows,
        errorMessage: null,
        pendingCorrelationId: null,
        deferred: false,
        scrollbarState: { verticalMax: 0, horizontalMax: 0, disabled: true },
        startLine: 0,
        startCol: 0,
        characterOffset: 0,
        needsRefresh: false,
        gutterNumbers: null,
      });
      this.tabViewStates.set(updatedStates);

      // Start scrollbar polling — scan starts in QuickScanInProgress
      this.startScrollPolling(viewSessionId);
    });

    // Configure scan-complete with accumulate queue mode before subscribing
    this.messageBus.configure('scan-complete', { queueMode: 'accumulate' });
    this.scanCompleteSubscription = this.messageBus.subscribe('scan-complete', (msg: InboundMessage) => {
      this.handleScanComplete(msg.payload);
    });

    // Subscribe to get-view responses
    this.getViewSubscription = this.messageBus.subscribe('get-view', (msg: InboundMessage) => {
      this.handleViewResponse(msg);
    });

    // Subscribe to get-scroll-info responses
    this.scrollInfoSubscription = this.messageBus.subscribe('get-scroll-info', (msg: InboundMessage) => {
      this.handleScrollInfoResponse(msg);
    });

    // Subscribe to get-wrapped-line-count responses
    this.wrappedLineCountSubscription = this.messageBus.subscribe('get-wrapped-line-count', (msg: InboundMessage) => {
      this.handleWrappedLineCountResponse(msg.payload);
    });
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
    this.scanCompleteSubscription?.unsubscribe();
    this.getViewSubscription?.unsubscribe();
    this.scrollInfoSubscription?.unsubscribe();
    this.wrappedLineCountSubscription?.unsubscribe();
    this.stopScrollPolling();
  }

  // --- Actions ---

  triggerOpenFile(): void {
    if (this.pendingCorrelationId() !== null) return;
    // Set sentinel before send to prevent re-entry even if callback fires synchronously
    this.pendingCorrelationId.set('__pending__');

    // Include viewport dimensions in open-file payload
    const dims = this.viewDimensions();
    const rowCount = dims?.rowCount ?? 40;
    const colCount = dims?.colCount ?? 120;
    const payload = `${rowCount}\n${colCount}`;

    const correlationId = this.messageBus.send('open-file', payload);
    this.pendingCorrelationId.set(correlationId);
  }

  setTabPosition(position: TabPosition): void {
    this.tabPosition.set(position);
    this.persistTabPosition(position);
  }

  activateTab(tabId: string): void {
    const oldTab = this.activeTab();

    this.activeTabId.set(tabId);

    // Cancel deferred for the old tab when active tab changes
    if (oldTab) {
      const states = this.tabViewStates();
      const oldState = states.get(oldTab.viewSessionId);
      if (oldState?.deferred) {
        const updated = new Map(states);
        updated.set(oldTab.viewSessionId, { ...oldState, deferred: false });
        this.tabViewStates.set(updated);
      }
    }

    // Manage scrollbar polling on tab switch
    const newTab = this.activeTab();
    if (newTab) {
      const newState = this.tabViewStates().get(newTab.viewSessionId);
      if (newState && !newState.scanComplete) {
        // Scan still in progress for new tab — start polling
        this.startScrollPolling(newTab.viewSessionId);
      } else {
        // Scan complete (values already cached) or no state — stop polling
        this.stopScrollPolling();
      }
    } else {
      this.stopScrollPolling();
    }

    // Handle needsRefresh tabs (Req 4.5, 5.5): when wrap mode was toggled while
    // this tab was inactive, it needs a fresh view request in the current mode.
    const newState2 = newTab ? this.tabViewStates().get(newTab.viewSessionId) : null;
    if (newState2?.needsRefresh) {
      // Clear needsRefresh flag
      const updated = new Map(this.tabViewStates());
      updated.set(newTab!.viewSessionId, { ...newState2, needsRefresh: false });
      this.tabViewStates.set(updated);

      // Send appropriate view request based on current wrap mode
      if (this.wrapMode()) {
        this.sendWrappedViewRequest(newTab!.viewSessionId);
        this.requestWrappedLineCount(newTab!.viewSessionId);
      } else {
        this.sendStandardViewRequest(newTab!.viewSessionId);
      }
    } else if (this.wrapMode() && newTab) {
      // Tab activated in wrapped mode — request wrapped line count for scrollbar
      this.requestWrappedLineCount(newTab.viewSessionId);
      if (!newState2?.viewRows) {
        this.tryTriggerViewRequest();
      }
    } else if (!newState2?.viewRows) {
      // Only trigger a view request if the new tab does NOT already have cached rows.
      // Tabs with cached viewRows already display correct content (Req 5.5, 7.5);
      // thumb position restores automatically via computed signals reading startLine/startCol.
      this.tryTriggerViewRequest();
    }
  }

  closeTab(tabId: string): void {
    const currentTabs = this.tabs();
    const index = currentTabs.findIndex(t => t.id === tabId);
    if (index === -1) return;

    const closedTab = currentTabs[index];

    // Stop scrollbar polling if active for this tab
    if (this.scrollPollSessionId === closedTab.viewSessionId) {
      this.stopScrollPolling();
    }

    // Cancel pending/deferred for this tab (Requirement 2.7)
    const states = this.tabViewStates();
    const tabState = states.get(closedTab.viewSessionId);
    if (tabState) {
      // Cancel pending request if one exists
      if (tabState.pendingCorrelationId) {
        this.messageBus.cancel(tabState.pendingCorrelationId);
      }
      // Remove TabViewState entry
      const updatedStates = new Map(states);
      updatedStates.delete(closedTab.viewSessionId);
      this.tabViewStates.set(updatedStates);
    }

    // Send "close-file" message with viewSessionId (Requirement 7.7)
    if (closedTab.viewSessionId) {
      this.messageBus.send('close-file', closedTab.viewSessionId);
    }

    // Proceed with existing tab removal logic
    const wasActive = this.activeTabId() === tabId;
    const remaining = currentTabs.filter(t => t.id !== tabId);
    this.tabs.set(remaining);

    if (wasActive) {
      if (remaining.length === 0) {
        this.activeTabId.set(null);
      } else if (index < remaining.length) {
        // Right neighbor exists (same index in the shorter array)
        this.activeTabId.set(remaining[index].id);
      } else {
        // No right neighbor, fall back to left
        this.activeTabId.set(remaining[index - 1].id);
      }
    }
  }

  dismissError(): void {
    this.errorMessage.set(null);
  }

  sendExit(): void {
    this.messageBus.send('exit');
  }

  updateViewDimensions(dims: ViewDimensions): void {
    this.viewDimensions.set(dims);
    this.tryTriggerViewRequest();

    // On resize, request wrapped line count if wrap mode active
    if (this.wrapMode()) {
      const tab = this.activeTab();
      if (tab) {
        this.requestWrappedLineCount(tab.viewSessionId);
      }
    }
  }

  updateCharMetricsWidth(width: number): void {
    this.charMetricsWidth.set(width);
  }

  toggleWrapMode(): void {
    const newMode = !this.wrapMode();
    this.wrapMode.set(newMode);

    const tab = this.activeTab();
    if (!tab) return; // No active tab — just update state, no request

    // Reset Start_Col to 0 for active tab
    this.updateScrollPosition(tab.viewSessionId, undefined, 0);

    // Mark all non-active tabs as needing refresh
    const states = this.tabViewStates();
    const updated = new Map(states);
    for (const [sessionId, state] of updated.entries()) {
      if (sessionId !== tab.viewSessionId) {
        updated.set(sessionId, { ...state, needsRefresh: true });
      }
    }

    // Reset characterOffset for active tab
    const activeState = updated.get(tab.viewSessionId);
    if (activeState) {
      updated.set(tab.viewSessionId, { ...activeState, characterOffset: 0 });
    }
    this.tabViewStates.set(updated);

    // Send appropriate view request for active tab
    if (newMode) {
      this.sendWrappedViewRequest(tab.viewSessionId);
      // Request wrapped line count for scrollbar computation
      this.requestWrappedLineCount(tab.viewSessionId);
    } else {
      this.sendStandardViewRequest(tab.viewSessionId);
    }
  }

  // --- Private: scan-complete handling ---

  private handleScanComplete(viewSessionId: string): void {
    // Find the tab with this viewSessionId
    const tab = this.tabs().find(t => t.viewSessionId === viewSessionId);
    if (!tab) {
      // Discard scan-complete for unknown sessions silently
      return;
    }

    // Update scanComplete in TabViewState
    const currentStates = this.tabViewStates();
    const existing = currentStates.get(viewSessionId);
    if (existing) {
      const updated = new Map(currentStates);
      updated.set(viewSessionId, { ...existing, scanComplete: true });
      this.tabViewStates.set(updated);
    } else {
      const updated = new Map(currentStates);
      updated.set(viewSessionId, {
        scanComplete: true,
        viewRows: null,
        errorMessage: null,
        pendingCorrelationId: null,
        deferred: false,
        scrollbarState: { verticalMax: 0, horizontalMax: 0, disabled: true },
        startLine: 0,
        startCol: 0,
        characterOffset: 0,
        needsRefresh: false,
        gutterNumbers: null,
      });
      this.tabViewStates.set(updated);
    }

    // Perform one final get-scroll-info poll.
    // Do NOT stop polling here — let handleScrollInfoResponse stop it
    // when it sees the terminal scan state. Stopping here causes a race:
    // scrollPollSessionId becomes null before the response arrives,
    // so handleScrollInfoResponse discards it and scrollbar stays disabled.
    if (this.scrollPollSessionId === viewSessionId) {
      this.messageBus.send('get-scroll-info', viewSessionId);
    }

    this.tryTriggerViewRequest();
  }

  // --- Private: view response handling ---

  private handleViewResponse(msg: InboundMessage): void {
    // Find which tab has a matching pendingCorrelationId
    const states = this.tabViewStates();
    let matchedSessionId: string | null = null;

    for (const [sessionId, state] of states.entries()) {
      if (state.pendingCorrelationId === msg.correlationId) {
        matchedSessionId = sessionId;
        break;
      }
    }

    // If no matching tab found, discard (tab may have been closed)
    if (!matchedSessionId) return;

    const currentState = states.get(matchedSessionId)!;
    const updated = new Map(states);

    if (msg.payload.startsWith(ShellStateService.ERROR_PREFIX)) {
      // Error response: store error, keep previous viewRows visible (Req 4.7, 5.6)
      updated.set(matchedSessionId, {
        ...currentState,
        errorMessage: msg.payload,
        pendingCorrelationId: null,
      });
    } else {
      // Success response: parse line numbers from backend response format
      const dims = this.viewDimensions();
      if (this.wrapMode() && dims) {
        // Wrapped mode: response format is "L:{n1},{n2},...\n{content}"
        const headerEnd = msg.payload.indexOf('\n');
        if (headerEnd === -1 || !msg.payload.startsWith('L:')) {
          // Malformed wrapped response — log error, keep previous state
          console.error('Malformed wrapped view response: missing L: header');
          updated.set(matchedSessionId, {
            ...currentState,
            pendingCorrelationId: null,
          });
        } else {
          const header = msg.payload.substring(2, headerEnd);
          const content = msg.payload.substring(headerEnd + 1);
          const gutterNumbers = header.split(',').map(v => v === '' ? null : parseInt(v, 10));
          const rows = splitIntoVisualRows(content, dims.colCount);
          updated.set(matchedSessionId, {
            ...currentState,
            viewRows: rows,
            gutterNumbers,
            errorMessage: null,
            pendingCorrelationId: null,
          });
        }
      } else {
        // Non-wrapped mode: each row is "{lineNum}\t{content}"
        const rawRows = msg.payload.split('\n');
        const parsedRows: string[] = [];
        const gutterNumbers: (number | null)[] = [];
        let malformed = false;
        for (const row of rawRows) {
          const tabIdx = row.indexOf('\t');
          if (tabIdx === -1) {
            // Malformed non-wrapped response — log error, keep previous state
            console.error('Malformed non-wrapped view response: missing tab separator');
            malformed = true;
            break;
          }
          gutterNumbers.push(parseInt(row.substring(0, tabIdx), 10));
          parsedRows.push(row.substring(tabIdx + 1));
        }
        if (malformed) {
          updated.set(matchedSessionId, {
            ...currentState,
            pendingCorrelationId: null,
          });
        } else {
          updated.set(matchedSessionId, {
            ...currentState,
            viewRows: parsedRows,
            gutterNumbers,
            errorMessage: null,
            pendingCorrelationId: null,
          });
        }
      }
    }

    this.tabViewStates.set(updated);
  }

  // --- Private: view request orchestration ---

  private tryTriggerViewRequest(): void {
    // 1. Get the active tab
    const tab = this.activeTab();
    // 2. If no active tab → return
    if (!tab) return;

    // 3. Get the TabViewState for the active tab's viewSessionId
    const states = this.tabViewStates();
    const state = states.get(tab.viewSessionId);

    // 4. If no state or not scanComplete → return
    if (!state || !state.scanComplete) return;

    // 5. Get current viewDimensions
    const dims = this.viewDimensions();

    // 6. If dimensions null → set deferred=true on the state, return
    if (!dims) {
      if (!state.deferred) {
        const updated = new Map(states);
        updated.set(tab.viewSessionId, { ...state, deferred: true });
        this.tabViewStates.set(updated);
      }
      return;
    }

    // 7. If pendingCorrelationId is non-null → return (duplicate suppression)
    if (state.pendingCorrelationId !== null) return;

    // 8. Clear deferred flag before sending
    if (state.deferred) {
      const updated = new Map(states);
      updated.set(tab.viewSessionId, { ...state, deferred: false });
      this.tabViewStates.set(updated);
    }

    // 9. Dispatch wrapped or standard request based on wrapMode
    if (this.wrapMode()) {
      this.sendWrappedViewRequest(tab.viewSessionId);
    } else {
      this.sendStandardViewRequest(tab.viewSessionId);
    }
  }

  // --- Private: scrollbar polling ---

  startScrollPolling(viewSessionId: string): void {
    this.stopScrollPolling();
    this.scrollPollSessionId = viewSessionId;
    this.scrollPollTimer = setInterval(() => {
      if (this.scrollPollSessionId) {
        this.messageBus.send('get-scroll-info', this.scrollPollSessionId);
      }
    }, 100);
    // Immediate first poll
    this.messageBus.send('get-scroll-info', viewSessionId);
  }

  stopScrollPolling(): void {
    if (this.scrollPollTimer !== null) {
      clearInterval(this.scrollPollTimer);
      this.scrollPollTimer = null;
    }
    this.scrollPollSessionId = null;
  }

  private handleScrollInfoResponse(msg: InboundMessage): void {
    const payload = msg.payload;
    if (payload.startsWith('ERROR:')) return;

    // Parse: scanState\nlineCount\nmaxByteLength\nmaxCharLength
    const fields = payload.split('\n');
    if (fields.length !== 4) return;

    const scanState = fields[0] as ScanStateValue;
    const lineCount = parseInt(fields[1], 10);
    const maxByteLength = parseInt(fields[2], 10);
    const maxCharLength = parseInt(fields[3], 10);

    if (isNaN(lineCount) || isNaN(maxByteLength) || isNaN(maxCharLength)) return;

    const horizontalMax = computeHorizontalMax(scanState, maxByteLength, maxCharLength);
    const verticalMax = lineCount;
    const disabled = verticalMax === 0 && horizontalMax === 0;

    const sessionId = this.scrollPollSessionId;
    if (!sessionId) return;

    this.updateTabScrollbar(sessionId, { verticalMax, horizontalMax, disabled });

    // Stop polling if scan reached terminal state
    if (scanState === 'QuickScanComplete' || scanState === 'FullScanComplete'
        || scanState === 'Failed' || scanState === 'Cancelled') {
      this.stopScrollPolling();

      // On failure/cancel, set scrollbar to zero
      if (scanState === 'Failed' || scanState === 'Cancelled') {
        this.updateTabScrollbar(sessionId, { verticalMax: 0, horizontalMax: 0, disabled: true });
      }

      // Request wrapped line count for scrollbar computation
      if (scanState !== 'Failed' && scanState !== 'Cancelled' && this.wrapMode()) {
        this.requestWrappedLineCount(sessionId);
      }
    }
  }

  private updateTabScrollbar(sessionId: string, scrollbarState: ScrollbarState): void {
    const currentStates = this.tabViewStates();
    const existing = currentStates.get(sessionId);
    if (!existing) return;

    const updated = new Map(currentStates);
    updated.set(sessionId, { ...existing, scrollbarState });
    this.tabViewStates.set(updated);
  }

  /** Send get-wrapped-line-count request for the given session */
  private requestWrappedLineCount(sessionId: string): void {
    const dims = this.viewDimensions();
    if (!dims) return;
    const payload = `${sessionId}\n${dims.colCount}`;
    this.messageBus.send('get-wrapped-line-count', payload);
  }

  /** Handle get-wrapped-line-count response: parse and set verticalMax */
  handleWrappedLineCountResponse(payload: string): void {
    if (payload.startsWith('ERROR:')) {
      const tab = this.activeTab();
      if (!tab) return;
      const state = this.tabViewStates().get(tab.viewSessionId);
      if (!state) return;
      this.updateTabScrollbar(tab.viewSessionId, {
        verticalMax: 0,
        horizontalMax: state.scrollbarState.horizontalMax,
        disabled: state.scrollbarState.horizontalMax === 0,
      });
      return;
    }
    const value = parseInt(payload, 10);
    const verticalMax = isNaN(value) || value < 0 ? 0 : value;
    const tab = this.activeTab();
    if (!tab) return;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return;
    this.updateTabScrollbar(tab.viewSessionId, {
      verticalMax,
      horizontalMax: state.scrollbarState.horizontalMax,
      disabled: verticalMax === 0 && state.scrollbarState.horizontalMax === 0,
    });
  }

  // --- Scroll action methods ---

  handleArrowKey(direction: 'up' | 'down' | 'left' | 'right'): void {
    if (this.wrapMode() && (direction === 'up' || direction === 'down')) {
      const tab = this.activeTab();
      if (!tab) return;
      const state = this.tabViewStates().get(tab.viewSessionId);
      if (!state) return;
      const dims = this.viewDimensions();
      if (!dims) return;
      const sb = state.scrollbarState;
      if (sb.disabled || sb.verticalMax <= dims.rowCount) return;

      const maxScroll = sb.verticalMax - dims.rowCount;
      const steps = direction === 'down' ? ARROW_STEP : -ARROW_STEP;
      const newStartLine = clamp(state.startLine + steps, 0, maxScroll);

      if (newStartLine === state.startLine) return;

      // Update state — startLine is visual row index, characterOffset reset to 0
      const updated = new Map(this.tabViewStates());
      updated.set(tab.viewSessionId, { ...state, startLine: newStartLine, characterOffset: 0 });
      this.tabViewStates.set(updated);

      this.sendWrappedViewRequest(tab.viewSessionId);
      return;
    }

    const tab = this.activeTab();
    if (!tab) return;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return;
    const sb = state.scrollbarState;
    const dims = this.viewDimensions();
    if (!dims || sb.disabled) return;

    let newStartLine = state.startLine;
    let newStartCol = state.startCol;

    switch (direction) {
      case 'down':
        if (sb.verticalMax > dims.rowCount)
          newStartLine = clamp(state.startLine + 1, 0, sb.verticalMax - dims.rowCount);
        break;
      case 'up':
        if (sb.verticalMax > dims.rowCount)
          newStartLine = clamp(state.startLine - 1, 0, sb.verticalMax - dims.rowCount);
        break;
      case 'right':
        if (sb.horizontalMax > dims.colCount)
          newStartCol = clamp(state.startCol + 1, 0, sb.horizontalMax - dims.colCount);
        break;
      case 'left':
        if (sb.horizontalMax > dims.colCount)
          newStartCol = clamp(state.startCol - 1, 0, sb.horizontalMax - dims.colCount);
        break;
    }

    if (newStartLine === state.startLine && newStartCol === state.startCol) return;

    this.updateScrollPosition(tab.viewSessionId, newStartLine, newStartCol);
    this.sendScrollViewRequest(tab.viewSessionId, newStartLine, newStartCol);
  }

  handleWheel(deltaY: number, deltaX: number): void {
    if (this.wrapMode()) {
      const tab = this.activeTab();
      if (!tab) return;
      const state = this.tabViewStates().get(tab.viewSessionId);
      if (!state) return;
      const dims = this.viewDimensions();
      if (!dims) return;
      const sb = state.scrollbarState;
      if (sb.disabled || sb.verticalMax <= dims.rowCount) return;

      const maxScroll = sb.verticalMax - dims.rowCount;
      const steps = deltaY > 0 ? WHEEL_STEP : -WHEEL_STEP;
      const newStartLine = clamp(state.startLine + steps, 0, maxScroll);

      if (newStartLine === state.startLine) return;

      // Update state — startLine is visual row index, characterOffset reset to 0
      const updated = new Map(this.tabViewStates());
      updated.set(tab.viewSessionId, { ...state, startLine: newStartLine, characterOffset: 0 });
      this.tabViewStates.set(updated);

      this.sendWrappedViewRequest(tab.viewSessionId);
      return;
    }

    const tab = this.activeTab();
    if (!tab) return;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return;
    const sb = state.scrollbarState;
    const dims = this.viewDimensions();
    if (!dims || sb.disabled) return;

    let newStartLine = state.startLine;
    let newStartCol = state.startCol;

    if (deltaY !== 0 && sb.verticalMax > dims.rowCount) {
      const maxScroll = sb.verticalMax - dims.rowCount;
      newStartLine = clamp(state.startLine + Math.sign(deltaY) * WHEEL_STEP, 0, maxScroll);
    }
    if (deltaX !== 0 && sb.horizontalMax > dims.colCount) {
      const maxScroll = sb.horizontalMax - dims.colCount;
      newStartCol = clamp(state.startCol + Math.sign(deltaX) * WHEEL_STEP, 0, maxScroll);
    }

    if (newStartLine === state.startLine && newStartCol === state.startCol) return;

    this.updateScrollPosition(tab.viewSessionId, newStartLine, newStartCol);
    this.sendScrollViewRequest(tab.viewSessionId, newStartLine, newStartCol);
  }

  // --- Drag action methods ---

  handleVerticalDragStart(mouseY: number, trackLength: number): void {
    if (trackLength <= 0) return;
    const tab = this.activeTab();
    if (!tab) return;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return;
    const sb = state.scrollbarState;
    const dims = this.viewDimensions();
    if (!dims || sb.verticalMax <= dims.rowCount) return;

    this.dragState.set({
      axis: 'vertical',
      startMousePos: mouseY,
      startScrollPos: state.startLine,
      trackLength,
      scrollbarMax: sb.verticalMax,
      viewportSize: dims.rowCount,
    });
  }

  handleHorizontalDragStart(mouseX: number, trackLength: number): void {
    if (trackLength <= 0) return;
    const tab = this.activeTab();
    if (!tab) return;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return;
    const sb = state.scrollbarState;
    const dims = this.viewDimensions();
    if (!dims || sb.horizontalMax <= dims.colCount) return;

    this.dragState.set({
      axis: 'horizontal',
      startMousePos: mouseX,
      startScrollPos: state.startCol,
      trackLength,
      scrollbarMax: sb.horizontalMax,
      viewportSize: dims.colCount,
    });
  }

  handleDragMove(mousePos: number): void {
    const drag = this.dragState();
    if (!drag) return;
    const tab = this.activeTab();
    if (!tab) return;

    const delta = mousePos - drag.startMousePos;
    const maxScroll = drag.scrollbarMax - drag.viewportSize;
    const scrollDelta = Math.round((delta / drag.trackLength) * maxScroll);
    const newPos = clamp(drag.startScrollPos + scrollDelta, 0, maxScroll);

    if (drag.axis === 'vertical') {
      if (this.wrapMode()) {
        // In wrap mode, startLine is visual row index; reset characterOffset to 0
        const states = this.tabViewStates();
        const existing = states.get(tab.viewSessionId);
        if (!existing) return;
        const updated = new Map(states);
        updated.set(tab.viewSessionId, { ...existing, startLine: newPos, characterOffset: 0 });
        this.tabViewStates.set(updated);
      } else {
        this.updateScrollPosition(tab.viewSessionId, newPos, undefined);
      }
    } else {
      this.updateScrollPosition(tab.viewSessionId, undefined, newPos);
    }
  }

  handleDragEnd(): void {
    const drag = this.dragState();
    if (!drag) return;
    this.dragState.set(null);

    const tab = this.activeTab();
    if (!tab) return;
    const state = this.tabViewStates().get(tab.viewSessionId);
    if (!state) return;

    if (this.wrapMode() && drag.axis === 'vertical') {
      this.sendWrappedViewRequest(tab.viewSessionId);
    } else {
      this.sendScrollViewRequest(tab.viewSessionId, state.startLine, state.startCol);
    }
  }

  // --- Private: scroll position update ---

  private updateScrollPosition(sessionId: string, startLine?: number, startCol?: number): void {
    const states = this.tabViewStates();
    const existing = states.get(sessionId);
    if (!existing) return;
    const updated = new Map(states);
    updated.set(sessionId, {
      ...existing,
      startLine: startLine ?? existing.startLine,
      startCol: startCol ?? existing.startCol,
    });
    this.tabViewStates.set(updated);
  }

  // --- Private: scroll view request with latest-wins cancellation ---

  private sendScrollViewRequest(sessionId: string, startLine: number, startCol: number): void {
    const states = this.tabViewStates();
    const existing = states.get(sessionId);
    if (!existing) return;

    // Latest-wins: cancel pending request if exists
    if (existing.pendingCorrelationId) {
      this.messageBus.cancel(existing.pendingCorrelationId);
    }

    const dims = this.viewDimensions();
    if (!dims) return;

    const payload = `${sessionId}\n${startLine}\n${startCol}\n${dims.rowCount}\n${dims.colCount}`;
    const correlationId = this.messageBus.send('get-view', payload);

    const updated = new Map(this.tabViewStates());
    updated.set(sessionId, {
      ...existing,
      startLine,
      startCol,
      pendingCorrelationId: correlationId,
    });
    this.tabViewStates.set(updated);
  }

  // --- Private: standard (non-wrapped) view request ---

  private sendStandardViewRequest(sessionId: string): void {
    const states = this.tabViewStates();
    const state = states.get(sessionId);
    if (!state) return;

    // Latest-wins: cancel pending request if exists
    if (state.pendingCorrelationId) {
      this.messageBus.cancel(state.pendingCorrelationId);
    }

    const dims = this.viewDimensions();
    if (!dims) return;

    const payload = `${sessionId}\n${state.startLine}\n${state.startCol}\n${dims.rowCount}\n${dims.colCount}`;
    const correlationId = this.messageBus.send('get-view', payload);

    const updated = new Map(this.tabViewStates());
    updated.set(sessionId, { ...state, pendingCorrelationId: correlationId });
    this.tabViewStates.set(updated);
  }

  // --- Private: wrapped-mode view request ---

  private sendWrappedViewRequest(sessionId: string): void {
    const states = this.tabViewStates();
    const state = states.get(sessionId);
    if (!state) return;

    // Latest-wins: cancel pending request if exists
    if (state.pendingCorrelationId) {
      this.messageBus.cancel(state.pendingCorrelationId);
    }

    const dims = this.viewDimensions();
    if (!dims) return;

    const characterCount = dims.colCount * dims.rowCount;
    // Cap at INT32_MAX
    const cappedCount = Math.min(characterCount, 2_147_483_647);

    // Payload: viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount\ncolCount
    const payload = `${sessionId}\nW\n${state.startLine}\n${state.characterOffset}\n${cappedCount}\n${dims.colCount}`;
    const correlationId = this.messageBus.send('get-view', payload);

    const updated = new Map(this.tabViewStates());
    updated.set(sessionId, { ...state, pendingCorrelationId: correlationId });
    this.tabViewStates.set(updated);
  }

  // --- Persistence ---

  private loadTabPosition(): TabPosition {
    try {
      const stored = localStorage.getItem(ShellStateService.TAB_POSITION_KEY);
      return stored === 'bottom' ? 'bottom' : 'top';
    } catch {
      return 'top';
    }
  }

  private persistTabPosition(position: TabPosition): void {
    try {
      localStorage.setItem(ShellStateService.TAB_POSITION_KEY, position);
    } catch {
      // best-effort, no-op on failure
    }
  }
}
