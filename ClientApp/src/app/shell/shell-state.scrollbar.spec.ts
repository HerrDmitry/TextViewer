/**
 * Unit tests for ShellStateService scrollbar behavior
 *
 * Validates: Requirements 9.1, 9.2, 9.3, 9.4, 9.5, 10.1, 10.2, 10.3, 10.4, 10.5, 10.6, 10.7,
 *            11.1, 11.2, 11.3, 11.4, 11.5, 11.6
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

import { ShellStateService, computeHorizontalMax } from './shell-state.service';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage } from '../services/message-bus.types';
import { ScanStateValue } from './shell.types';

describe('ShellStateService scrollbar behavior', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    jest.useFakeTimers();
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
    jest.useRealTimers();
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

  function simulateScrollInfoResponse(payload: string): void {
    const handler = mockSubscribeHandlers.get('get-scroll-info');
    handler!({
      messageType: 'get-scroll-info',
      correlationId: '',
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

  function openTab(filePath: string = '/test/file.txt'): string {
    service.triggerOpenFile();
    const viewSessionId = `vs-${uuidCounter + 1}`;
    simulateOpenFileResponse(`${viewSessionId}\n${filePath}`);
    return viewSessionId;
  }

  // --- computeHorizontalMax pure function tests ---

  describe('computeHorizontalMax', () => {
    it('returns maxByteLength for ScanInProgress', () => {
      expect(computeHorizontalMax('ScanInProgress', 500, 300)).toBe(500);
    });

    it('returns maxCharLength for ScanComplete', () => {
      expect(computeHorizontalMax('ScanComplete', 500, 420)).toBe(420);
    });

    it('returns 0 for NotStarted', () => {
      expect(computeHorizontalMax('NotStarted', 500, 300)).toBe(0);
    });

    it('returns 0 for Failed', () => {
      expect(computeHorizontalMax('Failed', 500, 300)).toBe(0);
    });

    it('returns 0 for Cancelled', () => {
      expect(computeHorizontalMax('Cancelled', 500, 300)).toBe(0);
    });
  });

  // --- handleScrollInfoResponse tests ---

  describe('handleScrollInfoResponse', () => {
    it('parses 4-field payload correctly and updates scrollbar state', () => {
      const vsId = openTab('/file.txt');

      // Simulate a valid scroll-info response while polling is active
      simulateScrollInfoResponse('ScanInProgress\n1000\n256\n0');

      const state = service.tabViewStates().get(vsId);
      expect(state!.scrollbarState.verticalMax).toBe(1000);
      expect(state!.scrollbarState.horizontalMax).toBe(256); // ScanInProgress → maxByteLength
      expect(state!.scrollbarState.disabled).toBe(false);
    });

    it('ignores ERROR: responses', () => {
      const vsId = openTab('/file.txt');
      const stateBefore = service.tabViewStates().get(vsId)!.scrollbarState;

      simulateScrollInfoResponse('ERROR:Session not found: xyz');

      const stateAfter = service.tabViewStates().get(vsId)!.scrollbarState;
      expect(stateAfter).toEqual(stateBefore);
    });

    it('ignores malformed payloads with wrong field count', () => {
      const vsId = openTab('/file.txt');
      const stateBefore = service.tabViewStates().get(vsId)!.scrollbarState;

      simulateScrollInfoResponse('ScanInProgress\n1000\n256');

      const stateAfter = service.tabViewStates().get(vsId)!.scrollbarState;
      expect(stateAfter).toEqual(stateBefore);
    });

    it('ignores malformed payloads with NaN values', () => {
      const vsId = openTab('/file.txt');
      const stateBefore = service.tabViewStates().get(vsId)!.scrollbarState;

      simulateScrollInfoResponse('ScanInProgress\nabc\n256\n0');

      const stateAfter = service.tabViewStates().get(vsId)!.scrollbarState;
      expect(stateAfter).toEqual(stateBefore);
    });
  });

  // --- startScrollPolling tests ---

  describe('startScrollPolling', () => {
    it('sends immediate first poll on open-file', () => {
      openTab('/file.txt');

      // The immediate first poll should have been sent
      const scrollInfoCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(scrollInfoCalls.length).toBe(1);
    });

    it('sends at 100ms intervals', () => {
      openTab('/file.txt');
      mockSend.mockClear();

      // Advance 100ms — should fire one interval poll
      jest.advanceTimersByTime(100);
      let scrollInfoCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(scrollInfoCalls.length).toBe(1);

      // Advance another 100ms — should fire another
      jest.advanceTimersByTime(100);
      scrollInfoCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(scrollInfoCalls.length).toBe(2);
    });
  });

  // --- stopScrollPolling tests ---

  describe('stopScrollPolling', () => {
    it('clears interval so no more polls are sent', () => {
      openTab('/file.txt');
      mockSend.mockClear();

      service.stopScrollPolling();

      jest.advanceTimersByTime(500);
      const scrollInfoCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(scrollInfoCalls.length).toBe(0);
    });
  });

  // --- Polling stops on terminal states ---

  describe('polling stops on terminal scan states', () => {
    it('stops polling on ScanComplete response', () => {
      openTab('/file.txt');
      mockSend.mockClear();

      // Simulate ScanComplete response
      simulateScrollInfoResponse('ScanComplete\n5000\n512\n400');

      // Advance time — no more polls should fire
      jest.advanceTimersByTime(500);
      const scrollInfoCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(scrollInfoCalls.length).toBe(0);
    });

    it('stops polling on Failed response and sets scrollbar to zero', () => {
      const vsId = openTab('/file.txt');
      mockSend.mockClear();

      // Simulate Failed response
      simulateScrollInfoResponse('Failed\n100\n50\n0');

      // Scrollbar should be set to zero
      const state = service.tabViewStates().get(vsId);
      expect(state!.scrollbarState.verticalMax).toBe(0);
      expect(state!.scrollbarState.horizontalMax).toBe(0);
      expect(state!.scrollbarState.disabled).toBe(true);

      // No more polls
      jest.advanceTimersByTime(500);
      const scrollInfoCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(scrollInfoCalls.length).toBe(0);
    });
  });

  // --- Tab switch polling lifecycle ---

  describe('tab switch stops old polling and starts new if in-progress', () => {
    it('stops polling for old tab and starts for new tab with in-progress scan', () => {
      // Open first tab (starts polling)
      const vsId1 = openTab('/file1.txt');
      // Mark first tab's scan as complete via scan-complete notification
      simulateScanComplete(vsId1);

      // Open second tab (starts polling for second tab)
      const vsId2 = openTab('/file2.txt');

      // Switch back to first tab (scan complete → stop polling)
      const tabs = service.tabs();
      mockSend.mockClear();
      service.activateTab(tabs[0].id);

      // Advance time — no polls should fire since first tab's scan is complete
      jest.advanceTimersByTime(300);
      const scrollInfoCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(scrollInfoCalls.length).toBe(0);

      // Switch to second tab (scan still in progress → start polling)
      mockSend.mockClear();
      service.activateTab(tabs[1].id);

      // Immediate poll on tab switch
      const immediatePolls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-scroll-info'
      );
      expect(immediatePolls.length).toBe(1);
    });
  });

  // --- activeScrollbarState computed signal ---

  describe('activeScrollbarState', () => {
    it('returns null when no active tab', () => {
      expect(service.activeScrollbarState()).toBeNull();
    });

    it('returns cached scrollbarState for active tab', () => {
      const vsId = openTab('/file.txt');

      // Simulate a scroll-info response to set scrollbar values
      simulateScrollInfoResponse('ScanInProgress\n2000\n128\n0');

      const scrollbar = service.activeScrollbarState();
      expect(scrollbar).not.toBeNull();
      expect(scrollbar!.verticalMax).toBe(2000);
      expect(scrollbar!.horizontalMax).toBe(128);
      expect(scrollbar!.disabled).toBe(false);
    });
  });

  // --- Scrollbar disabled state ---

  describe('scrollbar disabled state', () => {
    it('scrollbar disabled when verticalMax = 0 and horizontalMax = 0', () => {
      const vsId = openTab('/file.txt');

      // Simulate response with zero values (empty file)
      simulateScrollInfoResponse('ScanInProgress\n0\n0\n0');

      const state = service.tabViewStates().get(vsId);
      expect(state!.scrollbarState.verticalMax).toBe(0);
      expect(state!.scrollbarState.horizontalMax).toBe(0);
      expect(state!.scrollbarState.disabled).toBe(true);
    });
  });
});
