/**
 * Unit tests for ShellStateService
 *
 * Validates: Requirements 2.4, 2.6, 2.7, 2.8, 3.1, 3.2, 3.5, 3.6, 3.7, 3.8,
 *            4.2, 5.1, 6.1, 6.2, 7.1, 7.2, 7.3, 7.4, 7.5, 7.6
 */

// Polyfill crypto.randomUUID for jsdom
let uuidCounter = 0;
Object.defineProperty(globalThis, 'crypto', {
  value: {
    ...globalThis.crypto,
    randomUUID: () => {
      uuidCounter++;
      const hex = uuidCounter.toString(16).padStart(12, '0');
      return `00000000-0000-4000-8000-${hex}`;
    },
  },
  configurable: true,
});

// Mock MessageBusClient module to avoid bridge dependency
let mockSend: jest.Mock = jest.fn();
let mockSubscribeHandler: ((msg: any) => void) | null = null;
let mockSubscribeHandlers: Map<string, (msg: any) => void> = new Map();
let mockUnsubscribe: jest.Mock = jest.fn();

jest.mock('../services/message-bus-client.service', () => ({
  MessageBusClient: class MockMessageBusClient {
    send = (...args: any[]) => mockSend(...args);
    configure = jest.fn();
    subscribe = (messageType: string, handler: (msg: any) => void) => {
      mockSubscribeHandlers.set(messageType, handler);
      // Keep backward compat: mockSubscribeHandler points to 'open-file' handler
      if (messageType === 'open-file') {
        mockSubscribeHandler = handler;
      }
      return { unsubscribe: mockUnsubscribe };
    };
  },
}));

// Mock @angular/core to provide signal, computed, inject
let injectMap: Map<any, any> = new Map();

jest.mock('@angular/core', () => {
  function signal<T>(initialValue: T) {
    let value = initialValue;
    const fn = () => value;
    fn.set = (v: T) => { value = v; };
    fn.update = (updater: (v: T) => T) => { value = updater(value); };
    return fn;
  }

  function computed<T>(fn: () => T) {
    return fn;
  }

  function inject(token: any) {
    return injectMap.get(token);
  }

  return {
    Injectable: () => (target: any) => target,
    OnDestroy: class {},
    signal,
    computed,
    inject,
  };
});

import { ShellStateService } from './shell-state.service';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage } from '../services/message-bus.types';

describe('ShellStateService', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    uuidCounter = 0;
    correlationCounter = 0;
    mockSubscribeHandler = null;
    mockSubscribeHandlers = new Map();
    mockUnsubscribe = jest.fn();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  // --- Helper ---
  function simulateResponse(payload: string): void {
    const corrId = `corr-${correlationCounter}`;
    mockSubscribeHandler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload,
    } as InboundMessage);
  }

  // --- Initial State ---

  describe('Initial state', () => {
    it('tabs is empty array', () => {
      expect(service.tabs()).toEqual([]);
    });

    it('activeTabId is null', () => {
      expect(service.activeTabId()).toBeNull();
    });

    it('tabPosition defaults to "top" when localStorage is empty', () => {
      expect(service.tabPosition()).toBe('top');
    });

    it('tabPosition initializes from localStorage on construction', () => {
      // Create a new service with localStorage returning 'bottom'
      jest.spyOn(Storage.prototype, 'getItem').mockReturnValue('bottom');
      const mockBus = new MessageBusClient();
      injectMap.set(MessageBusClient, mockBus);
      const service2 = new ShellStateService();
      expect(service2.tabPosition()).toBe('bottom');
    });

    it('hasOpenTabs is false when no tabs', () => {
      expect(service.hasOpenTabs()).toBe(false);
    });

    it('isOpenFilePending is false initially', () => {
      expect(service.isOpenFilePending()).toBe(false);
    });

    it('errorMessage is null initially', () => {
      expect(service.errorMessage()).toBeNull();
    });
  });

  // --- triggerOpenFile ---

  describe('triggerOpenFile', () => {
    it('calls messageBus.send("open-file") with viewport dimensions payload', () => {
      service.triggerOpenFile();
      expect(mockSend).toHaveBeenCalledWith('open-file', '40\n120');
    });

    it('sets pendingCorrelationId after send', () => {
      service.triggerOpenFile();
      expect(service.pendingCorrelationId()).toBe('corr-1');
    });

    it('does nothing while pending (guard prevents duplicate sends)', () => {
      service.triggerOpenFile();
      expect(mockSend).toHaveBeenCalledTimes(1);

      service.triggerOpenFile();
      expect(mockSend).toHaveBeenCalledTimes(1);
      expect(service.pendingCorrelationId()).toBe('corr-1');
    });
  });

  // --- Tab creation from response ---

  describe('Tab creation on non-empty response', () => {
    it('creates a tab with correct filePath and fileName', () => {
      service.triggerOpenFile();
      simulateResponse('C:\\Users\\test\\document.txt');

      const tabs = service.tabs();
      expect(tabs.length).toBe(1);
      expect(tabs[0].filePath).toBe('C:\\Users\\test\\document.txt');
      expect(tabs[0].fileName).toBe('document.txt');
    });

    it('makes the new tab the active tab', () => {
      service.triggerOpenFile();
      simulateResponse('/home/user/file.ts');

      const tabs = service.tabs();
      expect(service.activeTabId()).toBe(tabs[0].id);
    });

    it('clears pendingCorrelationId after response', () => {
      service.triggerOpenFile();
      simulateResponse('/path/to/file.txt');

      expect(service.pendingCorrelationId()).toBeNull();
    });

    it('appends new tabs (does not replace existing)', () => {
      service.triggerOpenFile();
      simulateResponse('/file1.txt');

      service.triggerOpenFile();
      simulateResponse('/file2.txt');

      expect(service.tabs().length).toBe(2);
      expect(service.tabs()[0].fileName).toBe('file1.txt');
      expect(service.tabs()[1].fileName).toBe('file2.txt');
    });
  });

  // --- Empty response ---

  describe('Empty response (user cancelled)', () => {
    it('does not create a tab', () => {
      service.triggerOpenFile();
      simulateResponse('');

      expect(service.tabs().length).toBe(0);
    });

    it('preserves existing tabs', () => {
      service.triggerOpenFile();
      simulateResponse('/existing.txt');

      service.triggerOpenFile();
      simulateResponse('');

      expect(service.tabs().length).toBe(1);
      expect(service.tabs()[0].fileName).toBe('existing.txt');
    });

    it('clears pendingCorrelationId', () => {
      service.triggerOpenFile();
      simulateResponse('');

      expect(service.pendingCorrelationId()).toBeNull();
    });
  });

  // --- Error response ---

  describe('Error response handling', () => {
    it('sets errorMessage when payload starts with "ERROR:"', () => {
      service.triggerOpenFile();
      simulateResponse('ERROR: File not found');

      expect(service.errorMessage()).toBe('ERROR: File not found');
    });

    it('clears pendingCorrelationId on error', () => {
      service.triggerOpenFile();
      simulateResponse('ERROR: Access denied');

      expect(service.pendingCorrelationId()).toBeNull();
    });

    it('does not create a tab on error', () => {
      service.triggerOpenFile();
      simulateResponse('ERROR: Something went wrong');

      expect(service.tabs().length).toBe(0);
    });
  });

  // --- dismissError ---

  describe('dismissError', () => {
    it('clears errorMessage to null', () => {
      service.triggerOpenFile();
      simulateResponse('ERROR: Some error');
      expect(service.errorMessage()).toBe('ERROR: Some error');

      service.dismissError();
      expect(service.errorMessage()).toBeNull();
    });
  });

  // --- closeTab adjacency ---

  describe('closeTab', () => {
    beforeEach(() => {
      // Open 3 tabs: tab-1, tab-2, tab-3
      service.triggerOpenFile();
      simulateResponse('/a.txt');
      service.triggerOpenFile();
      simulateResponse('/b.txt');
      service.triggerOpenFile();
      simulateResponse('/c.txt');
    });

    it('removes the closed tab from the array', () => {
      const tabs = service.tabs();
      service.closeTab(tabs[1].id);

      expect(service.tabs().length).toBe(2);
      expect(service.tabs().find(t => t.id === tabs[1].id)).toBeUndefined();
    });

    it('selects right neighbor when closing active tab with right neighbor', () => {
      const tabs = service.tabs();
      // Activate middle tab
      service.activateTab(tabs[1].id);
      service.closeTab(tabs[1].id);

      // Right neighbor (tabs[2]) should be active
      expect(service.activeTabId()).toBe(tabs[2].id);
    });

    it('selects left neighbor when closing active tab at end (no right neighbor)', () => {
      const tabs = service.tabs();
      // Active is already the last tab (tabs[2]) from the last open
      expect(service.activeTabId()).toBe(tabs[2].id);
      service.closeTab(tabs[2].id);

      // Left neighbor (tabs[1]) should be active
      expect(service.activeTabId()).toBe(tabs[1].id);
    });

    it('sets activeTabId to null when closing the last remaining tab', () => {
      const tabs = service.tabs();
      service.closeTab(tabs[0].id);
      service.closeTab(tabs[1].id);
      service.closeTab(tabs[2].id);

      expect(service.tabs().length).toBe(0);
      expect(service.activeTabId()).toBeNull();
    });

    it('does not change activeTabId when closing a non-active tab', () => {
      const tabs = service.tabs();
      // Active is tabs[2] (last opened)
      expect(service.activeTabId()).toBe(tabs[2].id);

      service.closeTab(tabs[0].id);

      expect(service.activeTabId()).toBe(tabs[2].id);
    });

    it('no-ops when closing a tab that does not exist', () => {
      const tabsBefore = service.tabs();
      service.closeTab('nonexistent-id');

      expect(service.tabs()).toEqual(tabsBefore);
    });
  });

  // --- setTabPosition ---

  describe('setTabPosition', () => {
    it('persists value to localStorage', () => {
      service.setTabPosition('bottom');
      expect(localStorage.setItem).toHaveBeenCalledWith('tabPosition', 'bottom');
    });

    it('updates the tabPosition signal', () => {
      service.setTabPosition('bottom');
      expect(service.tabPosition()).toBe('bottom');

      service.setTabPosition('top');
      expect(service.tabPosition()).toBe('top');
    });
  });

  // --- Computed signals ---

  describe('Computed signals', () => {
    it('activeFilePath returns empty string when no tabs', () => {
      expect(service.activeFilePath()).toBe('');
    });

    it('activeFilePath returns the active tab filePath', () => {
      service.triggerOpenFile();
      simulateResponse('/home/user/readme.md');

      expect(service.activeFilePath()).toBe('/home/user/readme.md');
    });

    it('hasOpenTabs is true when tabs exist', () => {
      service.triggerOpenFile();
      simulateResponse('/file.txt');

      expect(service.hasOpenTabs()).toBe(true);
    });

    it('activeTab returns null when no tabs', () => {
      expect(service.activeTab()).toBeNull();
    });

    it('activeTab returns the active tab object', () => {
      service.triggerOpenFile();
      simulateResponse('/path/to/file.txt');

      const tab = service.activeTab();
      expect(tab).not.toBeNull();
      expect(tab!.filePath).toBe('/path/to/file.txt');
    });
  });

  // --- Unrelated correlation IDs ignored ---

  describe('Correlation ID filtering', () => {
    it('ignores messages with non-matching correlationId', () => {
      service.triggerOpenFile();

      // Send a message with a different correlationId
      mockSubscribeHandler!({
        messageType: 'open-file',
        correlationId: 'unrelated-corr-id',
        payload: '/should-not-appear.txt',
      } as InboundMessage);

      // No tab created, still pending
      expect(service.tabs().length).toBe(0);
      expect(service.pendingCorrelationId()).toBe('corr-1');
    });
  });

  // --- ngOnDestroy ---

  describe('ngOnDestroy', () => {
    it('calls unsubscribe on the subscription handle', () => {
      service.ngOnDestroy();
      expect(mockUnsubscribe).toHaveBeenCalled();
    });
  });
});
