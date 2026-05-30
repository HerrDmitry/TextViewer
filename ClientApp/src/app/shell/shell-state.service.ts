import { computed, Injectable, inject, OnDestroy, signal } from '@angular/core';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage, SubscriptionHandle } from '../services/message-bus.types';
import { extractFileName } from './extract-file-name';
import { Tab, TabPosition } from './shell.types';

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

  // --- Computed signals ---
  readonly activeTab = computed(() => {
    const id = this.activeTabId();
    return this.tabs().find(t => t.id === id) ?? null;
  });
  readonly activeFilePath = computed(() => this.activeTab()?.filePath ?? '');
  readonly hasOpenTabs = computed(() => this.tabs().length > 0);
  readonly isOpenFilePending = computed(() => this.pendingCorrelationId() !== null);

  // --- Dependencies ---
  private readonly messageBus = inject(MessageBusClient);
  private subscription: SubscriptionHandle | undefined;

  constructor() {
    this.subscription = this.messageBus.subscribe('open-file', (msg: InboundMessage) => {
      // Only process messages correlated to our pending request
      if (msg.correlationId !== this.pendingCorrelationId()) return;

      // Clear pending state on any correlated response
      this.pendingCorrelationId.set(null);

      // Error response (payload starts with "ERROR:")
      if (msg.payload.startsWith('ERROR:')) {
        this.errorMessage.set(msg.payload);
        return;
      }

      // Empty payload — user cancelled, no-op
      if (msg.payload === '') return;

      // Non-empty, non-error payload — create tab
      const newTab: Tab = {
        id: crypto.randomUUID(),
        filePath: msg.payload,
        fileName: extractFileName(msg.payload),
      };
      this.tabs.update(tabs => [...tabs, newTab]);
      this.activeTabId.set(newTab.id);
    });
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

  // --- Actions ---

  triggerOpenFile(): void {
    if (this.pendingCorrelationId() !== null) return;
    const correlationId = this.messageBus.send('open-file');
    this.pendingCorrelationId.set(correlationId);
  }

  setTabPosition(position: TabPosition): void {
    this.tabPosition.set(position);
    this.persistTabPosition(position);
  }

  activateTab(tabId: string): void {
    this.activeTabId.set(tabId);
  }

  closeTab(tabId: string): void {
    const currentTabs = this.tabs();
    const index = currentTabs.findIndex(t => t.id === tabId);
    if (index === -1) return;

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
