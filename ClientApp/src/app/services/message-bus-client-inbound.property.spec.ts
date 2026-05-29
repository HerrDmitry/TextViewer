/**
 * Property-based tests for MessageBusClient inbound routing and subscription.
 * Tasks 5.4–5.9
 */

// Mock Angular core to avoid ESM transform issues in Jest
jest.mock('@angular/core', () => ({
  Injectable: () => (target: any) => target,
  OnDestroy: class {},
}));

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

import * as fc from 'fast-check';
import { MessageBusClient } from './message-bus-client.service';
import { MessageProtocol } from './message-protocol';
import { InboundMessage } from './message-bus.types';

// Suppress console noise
beforeAll(() => {
  jest.spyOn(console, 'error').mockImplementation();
  jest.spyOn(console, 'warn').mockImplementation();
  jest.spyOn(console, 'debug').mockImplementation();
});

afterAll(() => {
  (console.error as jest.Mock).mockRestore();
  (console.warn as jest.Mock).mockRestore();
  (console.debug as jest.Mock).mockRestore();
});

// --- Shared generators ---

const messageTypeChars = 'abcdefghijklmnopqrstuvwxyz0123456789:-';

/** Generator for valid non-system Message_Type */
const validNonSystemType = fc
  .integer({ min: 1, max: 20 })
  .chain((len) =>
    fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...messageTypeChars.split('')) })
  )
  .filter((t) => !t.startsWith('system:'));

/** Generator for valid system Message_Type */
const validSystemType = fc
  .integer({ min: 1, max: 14 })
  .chain((len) =>
    fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...'abcdefghijklmnopqrstuvwxyz0123456789:-'.split('')) })
  )
  .map((suffix) => `system:${suffix}`);

/** Generator for valid Correlation_ID */
const correlationIdChars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-';
const validCorrelationId = fc
  .integer({ min: 1, max: 36 })
  .chain((len) =>
    fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...correlationIdChars.split('')) })
  );

/** Generator for payload */
const validPayload = fc.string({ minLength: 0, maxLength: 50 });

// --- Test setup helper ---

function createService(): { service: MessageBusClient; sendMessageMock: jest.Mock; receiveMessage: (raw: string) => void } {
  let capturedCallback: ((raw: string) => void) | null = null;
  const sendMessageMock = jest.fn();
  const receiveMessageMock = jest.fn((cb: (raw: string) => void) => { capturedCallback = cb; });
  Object.defineProperty(window, 'external', {
    value: { sendMessage: sendMessageMock, receiveMessage: receiveMessageMock },
    writable: true, configurable: true,
  });
  const service = new MessageBusClient();
  return { service, sendMessageMock, receiveMessage: capturedCallback! };
}

function cleanup(): void {
  delete (window as any).external;
}

/** Simulate inbound message delivery */
function simulateInbound(receiveMessage: (raw: string) => void, type: string, id: string, payload: string): void {
  receiveMessage(MessageProtocol.encode(type, id, payload));
}

// ============================================================================
// Property 4: Inbound routing correctness
// Validates: Requirements 1.10, 3.3, 3.4, 11.2
// ============================================================================

describe('Feature: message-bus-service, Property 4: Inbound routing correctness', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 1.10, 3.3, 3.4, 11.2**
   */
  it('delivers to subscribers when they exist, otherwise to fallback handler', async () => {
    await fc.assert(
      fc.asyncProperty(
        validNonSystemType,
        validCorrelationId,
        validPayload,
        fc.boolean(), // hasSubscribers
        fc.boolean(), // hasFallback
        async (msgType, corrId, payload, hasSubscribers, hasFallback) => {
          const { service, receiveMessage } = createService();

          const subscriberReceived: InboundMessage[] = [];
          const fallbackReceived: InboundMessage[] = [];

          if (hasSubscribers) {
            service.subscribe(msgType, (msg) => subscriberReceived.push(msg));
          }
          if (hasFallback) {
            service.setFallbackHandler((msg) => fallbackReceived.push(msg));
          }

          simulateInbound(receiveMessage, msgType, corrId, payload);
          await Promise.resolve();

          if (hasSubscribers) {
            // Subscribers receive the message
            expect(subscriberReceived.length).toBe(1);
            expect(subscriberReceived[0].messageType).toBe(msgType);
            expect(subscriberReceived[0].correlationId).toBe(corrId);
            expect(subscriberReceived[0].payload).toBe(payload);
            // Fallback does NOT receive when subscribers exist
            expect(fallbackReceived.length).toBe(0);
          } else if (hasFallback) {
            // Fallback receives when no subscribers
            expect(fallbackReceived.length).toBe(1);
            expect(fallbackReceived[0].messageType).toBe(msgType);
            expect(fallbackReceived[0].correlationId).toBe(corrId);
            expect(fallbackReceived[0].payload).toBe(payload);
            expect(subscriberReceived.length).toBe(0);
          } else {
            // No subscribers, no fallback → discarded
            expect(subscriberReceived.length).toBe(0);
            expect(fallbackReceived.length).toBe(0);
          }
        }
      ),
      { numRuns: 3 }
    );
  });
});

// ============================================================================
// Property 5: Subscriber error isolation
// Validates: Requirements 3.5, 13.5
// ============================================================================

describe('Feature: message-bus-service, Property 5: Subscriber error isolation', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 3.5, 13.5**
   */
  it('all non-throwing subscribers receive the message even when some throw', async () => {
    await fc.assert(
      fc.asyncProperty(
        validNonSystemType,
        validCorrelationId,
        validPayload,
        fc.integer({ min: 2, max: 6 }), // total subscribers N
        fc.integer({ min: 1, max: 5 }),  // throwers K (capped to N-1 below)
        async (msgType, corrId, payload, totalSubs, throwersRaw) => {
          const { service, receiveMessage } = createService();

          const throwers = Math.min(throwersRaw, totalSubs - 1); // ensure at least 1 non-thrower
          const received: number[] = []; // indices of subscribers that received

          for (let i = 0; i < totalSubs; i++) {
            if (i < throwers) {
              // Throwing subscriber
              service.subscribe(msgType, () => {
                throw new Error(`subscriber ${i} error`);
              });
            } else {
              // Non-throwing subscriber
              const idx = i;
              service.subscribe(msgType, () => {
                received.push(idx);
              });
            }
          }

          simulateInbound(receiveMessage, msgType, corrId, payload);
          await Promise.resolve();

          // All non-throwing subscribers received the message
          const expectedNonThrowers = totalSubs - throwers;
          expect(received.length).toBe(expectedNonThrowers);
        }
      ),
      { numRuns: 3 }
    );
  });
});

// ============================================================================
// Property 6: Per-type inbound delivery order
// Validates: Requirements 3.6
// ============================================================================

describe('Feature: message-bus-service, Property 6: Per-type inbound delivery order', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 3.6**
   */
  it('delivery order matches arrival order for same message type', async () => {
    await fc.assert(
      fc.asyncProperty(
        validNonSystemType,
        fc.array(
          fc.tuple(validCorrelationId, validPayload),
          { minLength: 2, maxLength: 10 }
        ),
        async (msgType, messages) => {
          const { service, receiveMessage } = createService();

          const deliveredIds: string[] = [];
          service.subscribe(msgType, (msg) => deliveredIds.push(msg.correlationId));

          // Send all inbound messages before microtask drain
          for (const [corrId, payload] of messages) {
            simulateInbound(receiveMessage, msgType, corrId, payload);
          }

          await Promise.resolve();

          // Delivery order matches arrival order
          const expectedIds = messages.map(([corrId]) => corrId);
          expect(deliveredIds).toEqual(expectedIds);
        }
      ),
      { numRuns: 3 }
    );
  });
});

// ============================================================================
// Property 7: Unsubscribe stops delivery
// Validates: Requirements 3.2
// ============================================================================

describe('Feature: message-bus-service, Property 7: Unsubscribe stops delivery', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 3.2**
   */
  it('no messages delivered after unsubscribe', async () => {
    await fc.assert(
      fc.asyncProperty(
        validNonSystemType,
        fc.integer({ min: 1, max: 5 }), // messages before unsubscribe
        fc.integer({ min: 1, max: 5 }), // messages after unsubscribe
        validPayload,
        async (msgType, beforeCount, afterCount, payload) => {
          const { service, receiveMessage } = createService();

          const received: InboundMessage[] = [];
          const handle = service.subscribe(msgType, (msg) => received.push(msg));

          // Send messages before unsubscribe
          for (let i = 0; i < beforeCount; i++) {
            simulateInbound(receiveMessage, msgType, `before-${i}`, payload);
          }
          await Promise.resolve();

          const receivedBeforeUnsub = received.length;
          expect(receivedBeforeUnsub).toBe(beforeCount);

          // Unsubscribe
          handle.unsubscribe();

          // Send messages after unsubscribe
          // Need a fallback so messages aren't just discarded at routing level
          const fallbackReceived: InboundMessage[] = [];
          service.setFallbackHandler((msg) => fallbackReceived.push(msg));

          for (let i = 0; i < afterCount; i++) {
            simulateInbound(receiveMessage, msgType, `after-${i}`, payload);
          }
          await Promise.resolve();

          // No additional messages delivered to the unsubscribed handler
          expect(received.length).toBe(receivedBeforeUnsub);
        }
      ),
      { numRuns: 3 }
    );
  });
});

// ============================================================================
// Property 13: System message inbound priority
// Validates: Requirements 10.2
// ============================================================================

describe('Feature: message-bus-service, Property 13: System message inbound priority', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 10.2**
   */
  it('system messages delivered before non-system messages when both queued before drain', async () => {
    await fc.assert(
      fc.asyncProperty(
        validSystemType,
        validNonSystemType,
        fc.integer({ min: 1, max: 3 }), // system message count
        fc.integer({ min: 1, max: 3 }), // normal message count
        async (sysType, normalType, sysCount, normalCount) => {
          const { service, receiveMessage } = createService();

          const deliveryOrder: string[] = []; // 'system' or 'normal'

          service.subscribe(sysType, () => deliveryOrder.push('system'));
          service.subscribe(normalType, () => deliveryOrder.push('normal'));

          // Send normal messages FIRST, then system messages
          // Both arrive before microtask drain
          for (let i = 0; i < normalCount; i++) {
            simulateInbound(receiveMessage, normalType, `normal-${i}`, '');
          }
          for (let i = 0; i < sysCount; i++) {
            simulateInbound(receiveMessage, sysType, `sys-${i}`, '');
          }

          await Promise.resolve();

          // All system messages should appear before all normal messages
          const totalExpected = sysCount + normalCount;
          expect(deliveryOrder.length).toBe(totalExpected);

          // First sysCount entries should all be 'system'
          for (let i = 0; i < sysCount; i++) {
            expect(deliveryOrder[i]).toBe('system');
          }
          // Remaining should be 'normal'
          for (let i = sysCount; i < totalExpected; i++) {
            expect(deliveryOrder[i]).toBe('normal');
          }
        }
      ),
      { numRuns: 3 }
    );
  });
});

// ============================================================================
// Property 15: Pending-request lifecycle
// Validates: Requirements 12.1, 12.3, 12.4, 12.5, 12.6
// ============================================================================

describe('Feature: message-bus-service, Property 15: Pending-request lifecycle', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 12.1, 12.3, 12.4, 12.5, 12.6**
   */
  it('response removes from pending and delivers; timeout removes and notifies; cancel removes silently', async () => {
    jest.useFakeTimers();

    await fc.assert(
      fc.asyncProperty(
        validNonSystemType,
        validPayload,
        fc.constantFrom('response', 'timeout', 'cancel') as fc.Arbitrary<'response' | 'timeout' | 'cancel'>,
        async (msgType, payload, action) => {
          let capturedCb: ((raw: string) => void) | null = null;
          const sendMessageMock = jest.fn();
          const receiveMessageMock = jest.fn((cb: (raw: string) => void) => { capturedCb = cb; });
          Object.defineProperty(window, 'external', {
            value: { sendMessage: sendMessageMock, receiveMessage: receiveMessageMock },
            writable: true, configurable: true,
          });
          const service = new MessageBusClient();
          const receiveMessage = capturedCb!;

          const timeoutMs = 5000;
          service.configure(msgType, { timeoutMs });

          const delivered: InboundMessage[] = [];
          service.subscribe(msgType, (msg) => delivered.push(msg));

          // Send creates a pending request
          const corrId = service.send(msgType, payload);
          expect(service._pendingRequests.has(corrId)).toBe(true);

          // Flush outbound dispatch
          await Promise.resolve();

          if (action === 'response') {
            // Simulate inbound response matching the correlationId
            simulateInbound(receiveMessage, msgType, corrId, 'response-payload');
            await Promise.resolve();

            // Pending removed
            expect(service._pendingRequests.has(corrId)).toBe(false);
            // Delivered to subscriber
            expect(delivered.some((m) => m.correlationId === corrId)).toBe(true);
          } else if (action === 'timeout') {
            // Advance time past timeout
            jest.advanceTimersByTime(timeoutMs + 1);
            await Promise.resolve();

            // Pending removed
            expect(service._pendingRequests.has(corrId)).toBe(false);
            // Timeout notification delivered to subscriber
            expect(delivered.some((m) => m.correlationId === corrId)).toBe(true);
          } else {
            // Cancel
            service.cancel(corrId);

            // Pending removed
            expect(service._pendingRequests.has(corrId)).toBe(false);
            // No delivery
            await Promise.resolve();
            expect(delivered.some((m) => m.correlationId === corrId)).toBe(false);
          }

          service.ngOnDestroy();
        }
      ),
      { numRuns: 3 }
    );

    jest.useRealTimers();
  });
});
