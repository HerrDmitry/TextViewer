/**
 * Feature: scan-progress-bar, Property 3: Scroll-info response parsing stores progress
 *
 * Validates: Requirements 3.2
 *
 * Property: For any valid 5-field scroll-info response payload where the first field
 * is 'ScanInProgress' and the fifth field is a parseable integer, the Shell_State_Service
 * SHALL store that integer as the session's scanProgress value.
 */

import * as fc from 'fast-check';

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

describe('Feature: scan-progress-bar, Property 3: Scroll-info response parsing stores progress', () => {
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

  function openTab(filePath: string = '/test/file.txt'): string {
    service.triggerOpenFile();
    const viewSessionId = `vs-${uuidCounter + 1}`;
    simulateOpenFileResponse(`${viewSessionId}\n${filePath}`);
    return viewSessionId;
  }

  it('stores parsed progress from 5-field ScanInProgress response', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 0, max: 1000000 }),   // lineCount
        fc.integer({ min: 0, max: 1000000 }),   // maxByteLength
        fc.integer({ min: 0, max: 1000000 }),   // maxCharLength
        fc.integer({ min: 0, max: 100 }),        // progress percentage
        (lineCount, maxByteLength, maxCharLength, progress) => {
          // Reset state for each run
          uuidCounter = 0;
          correlationCounter = 0;
          mockSend = jest.fn(() => `corr-${++correlationCounter}`);
          const mockBus = new MessageBusClient();
          injectMap.set(MessageBusClient, mockBus);
          service = new ShellStateService();

          const vsId = openTab('/test/file.txt');

          const payload = `ScanInProgress\n${lineCount}\n${maxByteLength}\n${maxCharLength}\n${progress}`;
          simulateScrollInfoResponse(payload);

          const state = service.tabViewStates().get(vsId);
          return state!.scanProgress === progress;
        }
      ),
      { numRuns: 10 }
    );
  });
});
