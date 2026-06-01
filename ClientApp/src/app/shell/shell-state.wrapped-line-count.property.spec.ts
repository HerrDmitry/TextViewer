/**
 * Feature: wrapped-line-count, Property 5: Response parsing validation
 *
 * **Validates: Requirements 3.4**
 *
 * Property: For any string response from the backend, the frontend handler SHALL set
 * verticalMax to the parsed integer if the string represents a valid non-negative integer,
 * and SHALL set verticalMax to 0 otherwise.
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
let mockSubscribeHandlers: Map<string, (msg: any) => void> = new Map();

jest.mock('../services/message-bus-client.service', () => ({
  MessageBusClient: class MockMessageBusClient {
    send = (...args: any[]) => mockSend(...args);
    configure = jest.fn();
    cancel = jest.fn();
    subscribe = (messageType: string, handler: (msg: any) => void) => {
      mockSubscribeHandlers.set(messageType, handler);
      return { unsubscribe: jest.fn() };
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

import * as fc from 'fast-check';
import { ShellStateService } from './shell-state.service';
import { MessageBusClient } from '../services/message-bus-client.service';

describe('Feature: wrapped-line-count, Property 5: Response parsing validation', () => {
  let service: ShellStateService;
  const SESSION_ID = 'test-session-001';

  beforeEach(() => {
    mockSubscribeHandlers = new Map();
    mockSend = jest.fn(() => 'corr-1');

    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();

    // Set up active tab with a TabViewState so handler can update scrollbar
    const tab = {
      id: 'tab-1',
      filePath: '/test/file.txt',
      fileName: 'file.txt',
      viewSessionId: SESSION_ID,
    };
    service.tabs.set([tab]);
    service.activeTabId.set('tab-1');
    const states = new Map();
    states.set(SESSION_ID, {
      scanComplete: true,
      viewRows: null,
      errorMessage: null,
      pendingCorrelationId: null,
      deferred: false,
      scrollbarState: { verticalMax: 0, horizontalMax: 5, disabled: false },
      startLine: 0,
      startCol: 0,
      characterOffset: 0,
      needsRefresh: false,
      gutterNumbers: null,
    });
    service.tabViewStates.set(states);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  /** Helper: get verticalMax from active tab's scrollbar state */
  function getVerticalMax(): number {
    const state = service.tabViewStates().get(SESSION_ID);
    return state?.scrollbarState.verticalMax ?? -1;
  }

  it('valid non-negative integers set verticalMax to parsed value; invalid strings set verticalMax to 0', () => {
    fc.assert(
      fc.property(
        fc.oneof(
          // Valid non-negative integers
          fc.nat({ max: 1_000_000 }).map(n => ({ payload: n.toString(), expected: n })),
          // Negative numbers → 0
          fc.integer({ min: -1_000_000, max: -1 }).map(n => ({ payload: n.toString(), expected: 0 })),
          // Floats → parseInt truncates, but if result < 0 → 0
          fc.double({ min: 0.1, max: 1_000_000, noNaN: true }).map(f => ({
            payload: f.toString(),
            expected: Math.floor(f) >= 0 ? Math.floor(f) : 0,
          })),
          // ERROR: prefixed → 0
          fc.string({ minLength: 0, maxLength: 20 }).map(s => ({
            payload: `ERROR: ${s}`,
            expected: 0,
          })),
          // Invalid strings (non-numeric) → 0
          fc.string({ minLength: 1, maxLength: 20 })
            .filter(s => isNaN(parseInt(s, 10)) && !s.startsWith('ERROR:'))
            .map(s => ({ payload: s, expected: 0 }))
        ),
        ({ payload, expected }) => {
          // Reset scrollbar state before each call
          const states = new Map(service.tabViewStates());
          const state = states.get(SESSION_ID)!;
          states.set(SESSION_ID, {
            ...state,
            scrollbarState: { ...state.scrollbarState, verticalMax: 999 },
          });
          service.tabViewStates.set(states);

          service.handleWrappedLineCountResponse(payload);

          return getVerticalMax() === expected;
        }
      ),
      { numRuns: 10 }
    );
  });
});
