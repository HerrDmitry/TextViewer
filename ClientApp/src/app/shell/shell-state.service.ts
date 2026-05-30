import { computed, Injectable, inject, OnDestroy, signal } from '@angular/core';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage, SubscriptionHandle } from '../services/message-bus.types';
import { extractFileName } from './extract-file-name';
import { Tab, TabPosition, TabViewState, ScrollbarState, ScanStateValue, ViewDimensions } from './shell.types';

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

  // --- Dependencies ---
  private readonly messageBus = inject(MessageBusClient);
  private subscription: SubscriptionHandle | undefined;
  private scanCompleteSubscription: SubscriptionHandle | undefined;
  private getViewSubscription: SubscriptionHandle | undefined;
  private scrollInfoSubscription: SubscriptionHandle | undefined;

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
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
    this.scanCompleteSubscription?.unsubscribe();
    this.getViewSubscription?.unsubscribe();
    this.scrollInfoSubscription?.unsubscribe();
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

    this.tryTriggerViewRequest();
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
      });
      this.tabViewStates.set(updated);
    }

    // Perform one final get-scroll-info poll, then stop polling
    if (this.scrollPollSessionId === viewSessionId) {
      this.messageBus.send('get-scroll-info', viewSessionId);
      this.stopScrollPolling();
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
      // Error response: store in errorMessage, clear viewRows
      updated.set(matchedSessionId, {
        ...currentState,
        errorMessage: msg.payload,
        viewRows: null,
        pendingCorrelationId: null,
      });
    } else {
      // Success response: split payload by \n and store in viewRows, clear errorMessage
      const rows = msg.payload.split('\n');
      updated.set(matchedSessionId, {
        ...currentState,
        viewRows: rows,
        errorMessage: null,
        pendingCorrelationId: null,
      });
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

    // 8. Send "get-view" with payload: viewSessionId\n0\n0\nrowCount\ncolCount
    const payload = `${tab.viewSessionId}\n0\n0\n${dims.rowCount}\n${dims.colCount}`;
    const correlationId = this.messageBus.send('get-view', payload);

    // 9. Store the correlationId in the TabViewState's pendingCorrelationId
    // 10. Clear deferred flag
    const updated = new Map(this.tabViewStates());
    updated.set(tab.viewSessionId, {
      ...state,
      pendingCorrelationId: correlationId,
      deferred: false,
    });
    this.tabViewStates.set(updated);
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
