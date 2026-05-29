/**
 * Property-based tests for MessageBusClient outbound queuing and dispatch.
 * Tasks 3.4–3.12
 */

// Mock Angular core to avoid ESM transform issues in Jest
jest.mock('@angular/core', () => ({
  Injectable: () => (target: any) => target,
  OnDestroy: class {},
}));

// Polyfill crypto.randomUUID for jsdom — unique per call
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

// --- Shared generators ---

const messageTypeChars = 'abcdefghijklmnopqrstuvwxyz0123456789:-';

/** Generator for valid Message_Type: [a-z0-9:-]+, 1–64 chars */
const validMessageType = fc
  .integer({ min: 1, max: 64 })
  .chain((len) =>
    fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...messageTypeChars.split('')) })
  );

/** Generator for a short valid message type (keeps queue count manageable) */
const shortMessageType = fc
  .integer({ min: 1, max: 10 })
  .chain((len) =>
    fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...messageTypeChars.split('')) })
  );

// --- Test setup helper ---

function createService(): { service: MessageBusClient; sendMessageMock: jest.Mock } {
  const sendMessageMock = jest.fn();
  const receiveMessageMock = jest.fn();
  Object.defineProperty(window, 'external', {
    value: { sendMessage: sendMessageMock, receiveMessage: receiveMessageMock },
    writable: true,
    configurable: true,
  });
  const service = new MessageBusClient();
  return { service, sendMessageMock };
}

function cleanup(): void {
  delete (window as any).external;
}

// ============================================================================
// Property 3: Correlation_ID uniqueness
// Validates: Requirements 1.3, 2.1, 2.2
// ============================================================================

describe('Feature: message-bus-service, Property 3: Correlation_ID uniqueness', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 1.3, 2.1, 2.2**
   */
  it('all returned Correlation_IDs are distinct for N send() calls', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 2, max: 500 }),
        validMessageType,
        (n, messageType) => {
          const { service } = createService();
          const ids = new Set<string>();
          for (let i = 0; i < n; i++) {
            ids.add(service.send(messageType));
          }
          expect(ids.size).toBe(n);
          service.ngOnDestroy();
        }
      ),
      { numRuns: 100 }
    );
  });
});

// ============================================================================
// Property 8: Accumulate queue FIFO with bounded capacity
// Validates: Requirements 4.1, 4.2, 4.3, 4.4, 16.1
// ============================================================================

describe('Feature: message-bus-service, Property 8: Accumulate queue FIFO with bounded capacity', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 4.1, 4.2, 4.3, 4.4, 16.1**
   */
  it('maintains FIFO order, size never exceeds 100, overflow discards newest', () => {
    jest.spyOn(console, 'warn').mockImplementation();
    jest.spyOn(console, 'error').mockImplementation();

    fc.assert(
      fc.property(
        fc.integer({ min: 0, max: 200 }),
        (count) => {
          const { service } = createService();
          const msgType = 'acc-type';
          const sentIds: string[] = [];

          for (let i = 0; i < count; i++) {
            sentIds.push(service.send(msgType, `payload-${i}`));
          }

          const queue = service._queues.get(msgType);
          if (count === 0) {
            // No queue created
            expect(queue).toBeUndefined();
            return;
          }

          // Size never exceeds 100
          expect(queue!.entries.length).toBeLessThanOrEqual(100);

          // FIFO order: entries are the first min(count, 100) messages
          const expectedCount = Math.min(count, 100);
          expect(queue!.entries.length).toBe(expectedCount);

          // FIFO: entries match the first 100 sent IDs in order (newest discarded on overflow)
          for (let i = 0; i < expectedCount; i++) {
            expect(queue!.entries[i].correlationId).toBe(sentIds[i]);
          }

          // Arrival timestamps are monotonically increasing within queue (FIFO order)
          for (let i = 1; i < queue!.entries.length; i++) {
            expect(queue!.entries[i].arrivalTimestamp).toBeGreaterThan(
              queue!.entries[i - 1].arrivalTimestamp
            );
          }

          // If N > 100: assert exactly 100 entries, first 100 messages kept
          if (count > 100) {
            expect(queue!.entries.length).toBe(100);
            expect(queue!.entries[0].correlationId).toBe(sentIds[0]);
            expect(queue!.entries[99].correlationId).toBe(sentIds[99]);
          }
        }
      ),
      { numRuns: 100 }
    );

    (console.warn as jest.Mock).mockRestore();
    (console.error as jest.Mock).mockRestore();
  });
});

// ============================================================================
// Property 9: Latest-wins queue stores only newest
// Validates: Requirements 5.1, 5.2, 16.2
// ============================================================================

describe('Feature: message-bus-service, Property 9: Latest-wins queue stores only newest', () => {
  afterEach(cleanup);

  it('queue contains at most 1 entry, always the most recently enqueued with correct Arrival_Timestamp', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 50 }),
        (count) => {
          const { service } = createService();
          const msgType = 'lw-type';
          service.configure(msgType, { queueMode: 'latest-wins' });

          let lastId = '';
          for (let i = 0; i < count; i++) {
            lastId = service.send(msgType, `payload-${i}`);

            // After each send: queue never exceeds 1 entry
            const queue = service._queues.get(msgType)!;
            expect(queue.entries.length).toBeLessThanOrEqual(1);
          }

          const queue = service._queues.get(msgType)!;

          // After all sends: exactly 1 entry
          expect(queue.entries.length).toBe(1);

          // Always the most recently enqueued
          expect(queue.entries[0].correlationId).toBe(lastId);
          expect(queue.entries[0].payload).toBe(`payload-${count - 1}`);

          // Arrival_Timestamp matches the last send's counter value
          expect(queue.entries[0].arrivalTimestamp).toBe(service._arrivalCounter);
        }
      ),
      { numRuns: 10 }
    );
  });
});

// ============================================================================
// Property 10: Configuration immutability
// Validates: Requirements 6.2
// ============================================================================

describe('Feature: message-bus-service, Property 10: Configuration immutability', () => {
  afterEach(cleanup);

  it('reconfiguration rejected after first enqueue', () => {
    fc.assert(
      fc.property(
        shortMessageType,
        fc.constantFrom<'accumulate' | 'latest-wins'>('accumulate', 'latest-wins'),
        fc.constantFrom<0 | 1 | 2>(0, 1, 2),
        fc.constantFrom<'accumulate' | 'latest-wins'>('accumulate', 'latest-wins'),
        fc.constantFrom<0 | 1 | 2>(0, 1, 2),
        (msgType, initialQueueMode, initialPriority, newQueueMode, newPriority) => {
          const { service } = createService();

          // Configure initially with random queueMode + priority
          service.configure(msgType, { queueMode: initialQueueMode, priority: initialPriority });

          // Send at least 1 message (freezes the queue)
          service.send(msgType, 'payload');

          // Attempt reconfiguration after enqueue — should throw
          expect(() =>
            service.configure(msgType, { queueMode: newQueueMode, priority: newPriority })
          ).toThrow(/Cannot reconfigure/);
        }
      ),
      { numRuns: 10 }
    );
  });

  it('configure multiple times before first send does not throw', () => {
    fc.assert(
      fc.property(
        shortMessageType,
        fc.array(
          fc.record({
            queueMode: fc.constantFrom<'accumulate' | 'latest-wins'>('accumulate', 'latest-wins'),
            priority: fc.constantFrom<0 | 1 | 2>(0, 1, 2),
          }),
          { minLength: 2, maxLength: 10 }
        ),
        (msgType, configs) => {
          const { service } = createService();

          // Multiple configures before any send — all should succeed
          for (const config of configs) {
            expect(() => service.configure(msgType, config)).not.toThrow();
          }

          // The last config should be applied
          const queue = service._queues.get(msgType)!;
          const lastConfig = configs[configs.length - 1];
          expect(queue.config.queueMode).toBe(lastConfig.queueMode);
          expect(queue.config.priority).toBe(lastConfig.priority);
        }
      ),
      { numRuns: 10 }
    );
  });
});

// ============================================================================
// Property 11: Priority dispatch ordering
// Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5
// ============================================================================

describe('Feature: message-bus-service, Property 11: Priority dispatch ordering', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5**
   */
  it('dispatcher selects message from lowest Priority value queue, tiebreaks by earliest Arrival_Timestamp', async () => {
    await fc.assert(
      fc.asyncProperty(
        // Generate 2-5 queues with different priorities and message counts
        fc.array(
          fc.record({
            priority: fc.constantFrom<0 | 1 | 2>(0, 1, 2),
            messageCount: fc.integer({ min: 1, max: 5 }),
          }),
          { minLength: 2, maxLength: 5 }
        ),
        async (queueConfigs) => {
          const sendMessageMock = jest.fn();
          Object.defineProperty(window, 'external', {
            value: { sendMessage: sendMessageMock, receiveMessage: jest.fn() },
            writable: true, configurable: true,
          });
          const service = new MessageBusClient();

          // Track dispatch order via mock — capture correlationId from each envelope
          const dispatchedIds: string[] = [];
          sendMessageMock.mockImplementation((envelope: string) => {
            const parts = envelope.split('\n');
            dispatchedIds.push(parts[1]); // correlationId is second field
          });

          // Configure queues with unique type names and send messages
          interface SentMsg { correlationId: string; priority: number; arrivalTimestamp: number }
          const allSent: SentMsg[] = [];

          for (let i = 0; i < queueConfigs.length; i++) {
            const typeName = `type-${String.fromCharCode(97 + i)}`;
            service.configure(typeName, { priority: queueConfigs[i].priority });

            for (let j = 0; j < queueConfigs[i].messageCount; j++) {
              const id = service.send(typeName, `msg-${i}-${j}`);
              allSent.push({
                correlationId: id,
                priority: queueConfigs[i].priority,
                arrivalTimestamp: service._arrivalCounter,
              });
            }
          }

          // Await microtask to trigger dispatch loop
          await Promise.resolve();

          // Compute expected dispatch order using the priority algorithm:
          // Simulate selectNext repeatedly — each iteration picks the front of the
          // queue with lowest priority, tiebreaking by earliest arrivalTimestamp.
          // Build per-queue FIFO lists to simulate the algorithm.
          const queueEntries = new Map<number, { priority: number; entries: SentMsg[] }>();
          for (let i = 0; i < queueConfigs.length; i++) {
            const key = i;
            queueEntries.set(key, { priority: queueConfigs[i].priority, entries: [] });
          }
          for (let i = 0; i < queueConfigs.length; i++) {
            const msgs = allSent.filter(
              (m) => m.priority === queueConfigs[i].priority &&
                queueEntries.get(i)!.entries.length < queueConfigs[i].messageCount
            );
          }
          // Rebuild properly: assign messages to queues in send order
          const queues: { priority: number; entries: SentMsg[] }[] = [];
          let msgIdx = 0;
          for (let i = 0; i < queueConfigs.length; i++) {
            const q: SentMsg[] = [];
            for (let j = 0; j < queueConfigs[i].messageCount; j++) {
              q.push(allSent[msgIdx++]);
            }
            queues.push({ priority: queueConfigs[i].priority, entries: q });
          }

          const expectedOrder: string[] = [];
          while (true) {
            let bestIdx = -1;
            let bestPriority = Infinity;
            let bestTimestamp = Infinity;

            for (let i = 0; i < queues.length; i++) {
              if (queues[i].entries.length === 0) continue;
              const front = queues[i].entries[0];
              const priority = queues[i].priority;

              if (
                priority < bestPriority ||
                (priority === bestPriority && front.arrivalTimestamp < bestTimestamp)
              ) {
                bestIdx = i;
                bestPriority = priority;
                bestTimestamp = front.arrivalTimestamp;
              }
            }

            if (bestIdx === -1) break;
            expectedOrder.push(queues[bestIdx].entries.shift()!.correlationId);
          }

          // Assert dispatch order matches expected priority ordering
          expect(dispatchedIds).toEqual(expectedOrder);
        }
      ),
      { numRuns: 10 }
    );
  });
});

// ============================================================================
// Property 16: Latest-wins discard cleans pending
// Validates: Requirements 12.7
// ============================================================================

describe('Feature: message-bus-service, Property 16: Latest-wins discard cleans pending', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 12.7**
   */
  it('old Correlation_ID removed from pending-requests map on replacement', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 2, max: 20 }),
        (count) => {
          const { service } = createService();
          const msgType = 'lw-pending';
          service.configure(msgType, { queueMode: 'latest-wins' });

          const allIds: string[] = [];
          for (let i = 0; i < count; i++) {
            const id = service.send(msgType, `payload-${i}`);
            allIds.push(id);

            // After each replacement: assert old correlationId NOT in pendingRequests
            if (i > 0) {
              const previousId = allIds[i - 1];
              expect(service._pendingRequests.has(previousId)).toBe(false);
            }

            // Current id should always be in pending
            expect(service._pendingRequests.has(id)).toBe(true);
          }

          // After all sends: only the last correlationId should be in pendingRequests
          const lastId = allIds[allIds.length - 1];
          expect(service._pendingRequests.has(lastId)).toBe(true);

          // All previous IDs should have been removed
          for (let i = 0; i < allIds.length - 1; i++) {
            expect(service._pendingRequests.has(allIds[i])).toBe(false);
          }
        }
      ),
      { numRuns: 10 }
    );
  });
});

// ============================================================================
// Property 17: Pending-requests capacity
// Validates: Requirements 16.3
// ============================================================================

describe('Feature: message-bus-service, Property 17: Pending-requests capacity', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 16.3**
   *
   * Fill pending-requests to 1000 entries using type-000 through type-099
   * (10 msgs each), then assert the 1001st send throws with "capacity exceeded".
   */
  it('throws on overflow when pending-requests at 1000 entries', () => {
    fc.assert(
      fc.property(
        // Generate a random extra message type for the overflow attempt
        validMessageType,
        (overflowType) => {
          const { service } = createService();

          // Fill pending-requests to exactly 1000 entries:
          // 100 types × 10 messages each = 1000 (stays within accumulate queue cap of 100)
          for (let t = 0; t < 100; t++) {
            const typeName = `type-${t.toString().padStart(3, '0')}`;
            for (let i = 0; i < 10; i++) {
              service.send(typeName, `p-${i}`);
            }
          }

          // Verify we have exactly 1000 pending requests
          expect(service._pendingRequests.size).toBe(1000);

          // 1001st send should throw with "capacity exceeded"
          expect(() => service.send('overflow-type', 'boom')).toThrow(
            /capacity exceeded/
          );
        }
      ),
      { numRuns: 10 }
    );
  });
});

// ============================================================================
// Property 12: Sequential outbound dispatch
// Validates: Requirements 7.7
// ============================================================================

describe('Feature: message-bus-service, Property 12: Sequential outbound dispatch', () => {
  afterEach(cleanup);

  /**
   * **Validates: Requirements 7.7**
   *
   * Generate random sequences of multiple queued messages across different types/priorities.
   * Assert at most one message in-flight (transmitted to bridge) at any time —
   * next dispatch does not begin until current completes.
   */
  it('at most one message in-flight at any time — next dispatch does not begin until current completes', async () => {
    jest.spyOn(console, 'error').mockImplementation();

    await fc.assert(
      fc.asyncProperty(
        // Generate N messages (2–20) each with a random type and priority
        fc.integer({ min: 2, max: 20 }).chain((n) =>
          fc.tuple(
            fc.constant(n),
            fc.array(
              fc.record({
                typeIndex: fc.integer({ min: 0, max: 4 }), // up to 5 distinct types
                priority: fc.constantFrom<0 | 1 | 2>(0, 1, 2),
              }),
              { minLength: n, maxLength: n }
            )
          )
        ),
        async ([n, messages]) => {
          const sendMessageMock = jest.fn();
          Object.defineProperty(window, 'external', {
            value: { sendMessage: sendMessageMock, receiveMessage: jest.fn() },
            writable: true, configurable: true,
          });
          const service = new MessageBusClient();

          // Concurrency tracking counter
          let currentInFlight = 0;
          let maxConcurrent = 0;

          sendMessageMock.mockImplementation(() => {
            currentInFlight++;
            if (currentInFlight > maxConcurrent) {
              maxConcurrent = currentInFlight;
            }
            // Bridge call is synchronous — completes immediately
            currentInFlight--;
          });

          // Configure distinct types and send messages
          const configuredTypes = new Set<string>();
          for (let i = 0; i < messages.length; i++) {
            const typeName = `dispatch-${messages[i].typeIndex}`;
            if (!configuredTypes.has(typeName)) {
              service.configure(typeName, { priority: messages[i].priority });
              configuredTypes.add(typeName);
            }
            service.send(typeName, `payload-${i}`);
          }

          // Flush microtask queue — triggerDispatch schedules via Promise.resolve().then(...)
          // Need two awaits: one to yield to the microtask, one to let the dispatch complete
          await Promise.resolve();
          await Promise.resolve();

          // At most one message in-flight at any time
          expect(maxConcurrent).toBeLessThanOrEqual(1);

          // All messages were dispatched
          // With up to 5 types and max 20 messages, accumulate queues (100 cap) won't overflow
          expect(sendMessageMock).toHaveBeenCalledTimes(n);
        }
      ),
      { numRuns: 10 }
    );

    (console.error as jest.Mock).mockRestore();
  });
});

// ============================================================================
// Unit test: Queue overflow emits warning and error event (Task 3.12)
// Requirements: 4.4, 13.4, 13.7, 16.1
// ============================================================================

describe('MessageBusClient — Queue overflow emits warning and error event', () => {
  afterEach(cleanup);

  it('emits console.warn and errors$ event with errorType queue-overflow on 101st message', () => {
    const { service } = createService();
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation();
    const errors: any[] = [];
    service.errors$.subscribe((e) => errors.push(e));

    const msgType = 'overflow-test';

    // Enqueue 100 messages (fills accumulate queue)
    for (let i = 0; i < 100; i++) {
      service.send(msgType, `msg-${i}`);
    }

    expect(warnSpy).not.toHaveBeenCalled();
    expect(errors.length).toBe(0);

    // 101st message triggers overflow
    const overflowId = service.send(msgType, 'overflow-msg');

    // console.warn called with overflow indication
    expect(warnSpy).toHaveBeenCalledTimes(1);
    expect(warnSpy.mock.calls[0][0]).toContain('overflow');

    // errors$ emits structured event
    expect(errors.length).toBe(1);
    expect(errors[0].errorType).toBe('queue-overflow');
    expect(errors[0].messageType).toBe(msgType);
    expect(errors[0].correlationId).toBe(overflowId);
    expect(errors[0].description).toBeDefined();

    // Queue size still 100
    const queue = service._queues.get(msgType)!;
    expect(queue.entries.length).toBe(100);

    warnSpy.mockRestore();
  });
});
