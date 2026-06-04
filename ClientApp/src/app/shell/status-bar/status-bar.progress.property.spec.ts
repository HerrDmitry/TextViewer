/**
 * Feature: scan-progress-bar, Property 2: Fill width equals progress percentage
 *
 * Validates: Requirements 3.1
 *
 * Property: For any integer progress value in [0, 100], the progress bar fill
 * element's inline width style SHALL be exactly "{value}%".
 *
 * The template binding `[style.width.%]="activeScanProgress()"` produces
 * `style="width: {value}%"`. This test verifies the component exposes the
 * correct signal value that drives that binding for any valid progress integer.
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

jest.mock('../../services/message-bus-client.service', () => ({
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
import { ShellStateService } from '../shell-state.service';
import { MessageBusClient } from '../../services/message-bus-client.service';
import { TabViewState } from '../shell.types';

describe('Feature: scan-progress-bar, Property 2: Fill width equals progress percentage', () => {
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

  it('activeScanProgress() equals the stored scanProgress value for any integer in [0, 100]', () => {
    fc.assert(
      fc.property(fc.integer({ min: 0, max: 100 }), (progressValue) => {
        const viewSessionId = 'session-progress-test';
        const tabId = 'tab-progress-test';
        const tab = {
          id: tabId,
          filePath: '/test/file.txt',
          fileName: 'file.txt',
          viewSessionId,
        };
        service.tabs.set([tab]);
        service.activeTabId.set(tabId);

        const states = new Map<string, TabViewState>();
        states.set(viewSessionId, {
          scanComplete: false,
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
          scanProgress: progressValue,
        });
        service.tabViewStates.set(states);

        // The template uses [style.width.%]="activeScanProgress()"
        // Angular converts this to style="width: {value}%"
        // So the signal must return exactly the stored progress integer.
        const actual = service.activeScanProgress();
        return actual === progressValue;
      }),
      { numRuns: 10 }
    );
  });
});
