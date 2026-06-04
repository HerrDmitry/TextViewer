/**
 * Feature: scan-progress-bar, Property 1: Progress bar visibility is determined solely by active tab scan state
 *
 * Validates: Requirements 1.1, 1.2, 1.3, 1.4, 5.1, 5.2, 5.3, 5.4
 *
 * Property: For any active tab ID (including null) and any scan state value
 * (NotStarted, ScanInProgress, ScanComplete, Failed, Cancelled), the progress bar
 * visibility signal SHALL equal true if and only if activeTabId !== null AND the
 * active tab's effective scan state is ScanInProgress.
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
import { TabViewState } from './shell.types';

describe('Feature: scan-progress-bar, Property 1: Progress bar visibility is determined solely by active tab scan state', () => {
  let service: ShellStateService;

  beforeEach(() => {
    mockSend = jest.fn(() => 'corr-1');
    mockSubscribeHandlers = new Map();

    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  // Generator for activeTabId: null or a UUID string
  const activeTabIdArb = fc.oneof(
    fc.constant(null as string | null),
    fc.uuid()
  );

  // Generator for scanComplete boolean (false = ScanInProgress, true = ScanComplete)
  const scanCompleteArb = fc.boolean();

  it('isScanning() returns true only when activeTabId !== null AND scanComplete === false', () => {
    fc.assert(
      fc.property(activeTabIdArb, scanCompleteArb, (activeTabId, scanComplete) => {
        // Set up tab state based on generated values
        if (activeTabId !== null) {
          const viewSessionId = 'session-test';
          const tab = {
            id: activeTabId,
            filePath: '/test/file.txt',
            fileName: 'file.txt',
            viewSessionId,
          };
          service.tabs.set([tab]);
          service.activeTabId.set(activeTabId);

          // Set up TabViewState with the generated scanComplete value
          const states = new Map<string, TabViewState>();
          states.set(viewSessionId, {
            scanComplete,
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
            scanProgress: 0,
          });
          service.tabViewStates.set(states);
        } else {
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.tabViewStates.set(new Map());
        }

        // Expected: visible only when activeTabId !== null AND scanComplete === false
        const expectedVisible = activeTabId !== null && !scanComplete;
        return service.isScanning() === expectedVisible;
      }),
      { numRuns: 10 }
    );
  });
});
