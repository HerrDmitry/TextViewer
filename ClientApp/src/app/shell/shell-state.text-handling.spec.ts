/**
 * Unit tests for ShellStateService text-handling extensions
 *
 * Validates: Requirements 3.3, 3.4, 3.5, 7.2, 7.7, 2.4, 2.5, 2.7, 8.4, 8.6
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
let mockCancel: jest.Mock = jest.fn();
let mockConfigure: jest.Mock = jest.fn();
let mockSubscribeHandlers: Map<string, (msg: any) => void> = new Map();
let mockUnsubscribe: jest.Mock = jest.fn();

jest.mock('../services/message-bus-client.service', () => ({
  MessageBusClient: class MockMessageBusClient {
    send = (...args: any[]) => mockSend(...args);
    cancel = (...args: any[]) => mockCancel(...args);
    configure = (...args: any[]) => mockConfigure(...args);
    subscribe = (messageType: string, handler: (msg: any) => void) => {
      mockSubscribeHandlers.set(messageType, handler);
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

describe('ShellStateService text-handling extensions', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    uuidCounter = 0;
    correlationCounter = 0;
    mockSubscribeHandlers = new Map();
    mockUnsubscribe = jest.fn();
    mockCancel = jest.fn();
    mockConfigure = jest.fn();
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

  // --- Helpers ---

  function simulateOpenFileResponse(payload: string): void {
    const corrId = `corr-${correlationCounter}`;
    const handler = mockSubscribeHandlers.get('open-file');
    handler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload,
    } as InboundMessage);
  }

  function simulateScanComplete(viewSessionId: string): void {
    const handler = mockSubscribeHandlers.get('scan-complete');
    handler!({
      messageType: 'scan-complete',
      correlationId: '',
      payload: viewSessionId,
    } as InboundMessage);
  }

  function simulateGetViewResponse(correlationId: string, payload: string): void {
    const handler = mockSubscribeHandlers.get('get-view');
    handler!({
      messageType: 'get-view',
      correlationId,
      payload,
    } as InboundMessage);
  }

  function openTab(filePath: string = '/test/file.txt'): string {
    service.triggerOpenFile();
    const viewSessionId = `vs-${uuidCounter + 1}`;
    // The response format: viewSessionId\nfilePath
    simulateOpenFileResponse(`${viewSessionId}\n${filePath}`);
    return viewSessionId;
  }

  function openTabWithRows(filePath: string, rows: string[]): string {
    service.triggerOpenFile();
    const viewSessionId = `vs-${uuidCounter + 1}`;
    const payload = `${viewSessionId}\n${filePath}\n${rows.join('\n')}`;
    simulateOpenFileResponse(payload);
    return viewSessionId;
  }

  // --- scan-complete subscription configured with accumulate queue mode ---

  describe('scan-complete subscription configuration', () => {
    it('configures scan-complete with accumulate queue mode', () => {
      expect(mockConfigure).toHaveBeenCalledWith('scan-complete', { queueMode: 'accumulate' });
    });

    it('subscribes to scan-complete messages', () => {
      expect(mockSubscribeHandlers.has('scan-complete')).toBe(true);
    });
  });

  // --- scan-complete for unknown session discarded ---

  describe('scan-complete for unknown session', () => {
    it('discards scan-complete for unknown viewSessionId silently', () => {
      // No tabs open — scan-complete should be discarded without error
      simulateScanComplete('unknown-session-id');

      // No state changes, no errors
      expect(service.tabViewStates().size).toBe(0);
    });

    it('does not affect existing tabs when unknown session scan-complete arrives', () => {
      const vsId = openTab('/existing.txt');

      simulateScanComplete('completely-unknown-id');

      // Existing tab state unchanged
      const state = service.tabViewStates().get(vsId);
      expect(state).toBeDefined();
      expect(state!.scanComplete).toBe(false);
    });
  });

  // --- open-file response parsed: viewSessionId + filePath + Initial_View rows ---

  describe('open-file response with Initial_View rows', () => {
    it('parses viewSessionId, filePath, and rows from response', () => {
      const vsId = openTabWithRows('/path/to/file.txt', ['line 1', 'line 2', 'line 3']);

      const tabs = service.tabs();
      expect(tabs.length).toBe(1);
      expect(tabs[0].viewSessionId).toBe(vsId);
      expect(tabs[0].filePath).toBe('/path/to/file.txt');

      const state = service.tabViewStates().get(vsId);
      expect(state).toBeDefined();
      expect(state!.viewRows).toEqual(['line 1', 'line 2', 'line 3']);
    });

    it('stores Initial_View rows in TabViewState.viewRows immediately on open', () => {
      const vsId = openTabWithRows('/file.txt', ['row A', 'row B']);

      const state = service.tabViewStates().get(vsId);
      expect(state!.viewRows).toEqual(['row A', 'row B']);
      expect(state!.scanComplete).toBe(false);
      expect(state!.pendingCorrelationId).toBeNull();
    });
  });

  // --- open-file response with no rows: viewSessionId + filePath only ---

  describe('open-file response with no rows', () => {
    it('parses viewSessionId and filePath when no rows present', () => {
      const vsId = openTab('/path/to/empty.txt');

      const tabs = service.tabs();
      expect(tabs.length).toBe(1);
      expect(tabs[0].viewSessionId).toBe(vsId);
      expect(tabs[0].filePath).toBe('/path/to/empty.txt');

      const state = service.tabViewStates().get(vsId);
      expect(state).toBeDefined();
      expect(state!.viewRows).toBeNull();
    });
  });

  // --- triggerOpenFile sends viewport dimensions in payload ---

  describe('triggerOpenFile sends viewport dimensions', () => {
    it('sends rowCount\\ncolCount in payload when dimensions are available', () => {
      service.updateViewDimensions({ rowCount: 25, colCount: 80 });

      service.triggerOpenFile();

      expect(mockSend).toHaveBeenCalledWith('open-file', '25\n80');
    });

    it('sends fallback 40\\n120 when viewDimensions is null', () => {
      // viewDimensions starts as null
      service.triggerOpenFile();

      expect(mockSend).toHaveBeenCalledWith('open-file', '40\n120');
    });
  });

  // --- close-file sent on tab close with viewSessionId ---

  describe('close-file on tab close', () => {
    it('sends close-file message with viewSessionId when tab is closed', () => {
      const vsId = openTab('/file.txt');
      const tabs = service.tabs();

      mockSend.mockClear();
      service.closeTab(tabs[0].id);

      expect(mockSend).toHaveBeenCalledWith('close-file', vsId);
    });

    it('removes TabViewState entry on tab close', () => {
      const vsId = openTab('/file.txt');
      expect(service.tabViewStates().has(vsId)).toBe(true);

      const tabs = service.tabs();
      service.closeTab(tabs[0].id);

      expect(service.tabViewStates().has(vsId)).toBe(false);
    });
  });

  // --- Deferred request triggered when measurement completes ---

  describe('deferred request triggered when measurement completes', () => {
    it('sets deferred=true when scan-complete arrives but no dimensions', () => {
      const vsId = openTab('/file.txt');
      // Activate the tab (it's already active from open)
      // scan-complete arrives but viewDimensions is null
      simulateScanComplete(vsId);

      const state = service.tabViewStates().get(vsId);
      expect(state!.deferred).toBe(true);
      expect(state!.scanComplete).toBe(true);
    });

    it('sends get-view when dimensions arrive after deferred was set', () => {
      const vsId = openTab('/file.txt');
      simulateScanComplete(vsId);

      // Verify deferred is set
      expect(service.tabViewStates().get(vsId)!.deferred).toBe(true);

      mockSend.mockClear();
      correlationCounter = 10; // reset for clarity

      // Now provide dimensions — should trigger the deferred request
      service.updateViewDimensions({ rowCount: 30, colCount: 100 });

      expect(mockSend).toHaveBeenCalledWith('get-view', `${vsId}\n0\n0\n30\n100`);
    });

    it('clears deferred flag after sending get-view', () => {
      const vsId = openTab('/file.txt');
      simulateScanComplete(vsId);
      service.updateViewDimensions({ rowCount: 30, colCount: 100 });

      const state = service.tabViewStates().get(vsId);
      expect(state!.deferred).toBe(false);
      expect(state!.pendingCorrelationId).not.toBeNull();
    });
  });

  // --- Duplicate suppression (no send while pending) ---

  describe('duplicate suppression', () => {
    it('does not send get-view while a request is already pending for the tab', () => {
      const vsId = openTab('/file.txt');
      service.updateViewDimensions({ rowCount: 30, colCount: 100 });
      simulateScanComplete(vsId);

      // First get-view should have been sent
      const firstCallCount = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      ).length;
      expect(firstCallCount).toBe(1);

      // Trigger again (e.g., resize) — should be suppressed
      service.updateViewDimensions({ rowCount: 35, colCount: 110 });

      const secondCallCount = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      ).length;
      expect(secondCallCount).toBe(1); // still 1, no duplicate
    });

    it('allows new get-view after pending response is received', () => {
      const vsId = openTab('/file.txt');
      service.updateViewDimensions({ rowCount: 30, colCount: 100 });
      simulateScanComplete(vsId);

      // Get the pending correlationId
      const state = service.tabViewStates().get(vsId);
      const pendingCorrId = state!.pendingCorrelationId!;

      // Simulate response
      simulateGetViewResponse(pendingCorrId, 'row1\nrow2');

      // Now resize — should send a new get-view
      mockSend.mockClear();
      correlationCounter = 20;
      service.updateViewDimensions({ rowCount: 35, colCount: 110 });

      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(1);
    });
  });

  // --- Cancel on tab close ---

  describe('cancel on tab close', () => {
    it('cancels pending get-view request when tab is closed', () => {
      const vsId = openTab('/file.txt');
      service.updateViewDimensions({ rowCount: 30, colCount: 100 });
      simulateScanComplete(vsId);

      // Get the pending correlationId
      const state = service.tabViewStates().get(vsId);
      const pendingCorrId = state!.pendingCorrelationId!;
      expect(pendingCorrId).not.toBeNull();

      const tabs = service.tabs();
      service.closeTab(tabs[0].id);

      expect(mockCancel).toHaveBeenCalledWith(pendingCorrId);
    });

    it('cancels deferred request when tab is closed (no pending to cancel)', () => {
      const vsId = openTab('/file.txt');
      // scan-complete without dimensions → deferred
      simulateScanComplete(vsId);
      expect(service.tabViewStates().get(vsId)!.deferred).toBe(true);

      const tabs = service.tabs();
      service.closeTab(tabs[0].id);

      // TabViewState removed — deferred effectively cancelled
      expect(service.tabViewStates().has(vsId)).toBe(false);
    });

    it('does not process get-view response after tab is closed', () => {
      const vsId = openTab('/file.txt');
      service.updateViewDimensions({ rowCount: 30, colCount: 100 });
      simulateScanComplete(vsId);

      const state = service.tabViewStates().get(vsId);
      const pendingCorrId = state!.pendingCorrelationId!;

      const tabs = service.tabs();
      service.closeTab(tabs[0].id);

      // Simulate late response — should be discarded
      simulateGetViewResponse(pendingCorrId, 'late-row1\nlate-row2');

      // No TabViewState exists for this session anymore
      expect(service.tabViewStates().has(vsId)).toBe(false);
    });
  });
});
