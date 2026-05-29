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

import { MessageBusClient } from './message-bus-client.service';
import { MessageProtocol } from './message-protocol';

describe('MessageBusClient', () => {
  let service: MessageBusClient;
  let sendMessageMock: jest.Mock;
  let receiveMessageMock: jest.Mock;
  let inboundCallback: ((raw: string) => void) | null;

  beforeEach(() => {
    inboundCallback = null;
    // Mock the Photino bridge so dispatch doesn't throw
    sendMessageMock = jest.fn();
    receiveMessageMock = jest.fn((cb: (raw: string) => void) => { inboundCallback = cb; });
    Object.defineProperty(window, 'external', {
      value: { sendMessage: sendMessageMock, receiveMessage: receiveMessageMock },
      writable: true, configurable: true,
    });
    service = new MessageBusClient();
  });

  afterEach(() => {
    delete (window as any).external;
  });

  describe('configure()', () => {
    it('should store config for a message type', () => {
      service.configure('test-type', { queueMode: 'latest-wins', priority: 0, timeoutMs: 5000 });
      const queue = service._queues.get('test-type');
      expect(queue).toBeDefined();
      expect(queue!.config.queueMode).toBe('latest-wins');
      expect(queue!.config.priority).toBe(0);
      expect(queue!.config.timeoutMs).toBe(5000);
    });

    it('should reject invalid queue mode', () => {
      expect(() => {
        service.configure('test-type', { queueMode: 'invalid' as any });
      }).toThrow(/Invalid queue mode/);
    });

    it('should reject reconfiguration after first enqueue', () => {
      service.send('test-type', 'payload');
      expect(() => {
        service.configure('test-type', { priority: 0 });
      }).toThrow(/Cannot reconfigure/);
    });

    it('should allow reconfiguration before any enqueue', () => {
      service.configure('test-type', { priority: 0 });
      service.configure('test-type', { priority: 2 });
      const queue = service._queues.get('test-type');
      expect(queue!.config.priority).toBe(2);
    });

    it('should merge partial config with defaults', () => {
      service.configure('test-type', { priority: 0 });
      const queue = service._queues.get('test-type');
      expect(queue!.config.queueMode).toBe('accumulate');
      expect(queue!.config.priority).toBe(0);
      expect(queue!.config.timeoutMs).toBe(30000);
    });

    it('should accept accumulate queue mode', () => {
      expect(() => {
        service.configure('test-type', { queueMode: 'accumulate' });
      }).not.toThrow();
    });

    it('should accept latest-wins queue mode', () => {
      expect(() => {
        service.configure('test-type', { queueMode: 'latest-wins' });
      }).not.toThrow();
    });
  });

  describe('send()', () => {
    it('should return a non-empty correlation ID', () => {
      const id = service.send('test-type');
      expect(id).toBeTruthy();
      expect(id.length).toBeGreaterThan(0);
    });

    it('should return unique correlation IDs', () => {
      const ids = new Set<string>();
      for (let i = 0; i < 50; i++) {
        ids.add(service.send('test-type'));
      }
      expect(ids.size).toBe(50);
    });

    it('should be synchronous (returns immediately)', () => {
      const id = service.send('test-type', 'payload');
      expect(typeof id).toBe('string');
    });

    it('should throw on invalid message type', () => {
      expect(() => service.send('INVALID')).toThrow(/Invalid message type/);
      expect(() => service.send('')).toThrow(/Invalid message type/);
      expect(() => service.send('a'.repeat(65))).toThrow(/Invalid message type/);
    });

    it('should throw on invalid payload', () => {
      expect(() => service.send('test-type', 'x'.repeat(2_097_153))).toThrow(/Invalid payload/);
    });

    it('should not throw on valid payload at max length', () => {
      expect(() => service.send('test-type', 'x'.repeat(2_097_152))).not.toThrow();
    });

    it('should add to pending-requests map', () => {
      const id = service.send('test-type', 'payload');
      expect(service._pendingRequests.has(id)).toBe(true);
      const pending = service._pendingRequests.get(id)!;
      expect(pending.correlationId).toBe(id);
      expect(pending.messageType).toBe('test-type');
    });

    it('should assign monotonically increasing arrival timestamps', () => {
      service.send('type-a');
      service.send('type-b');
      service.send('type-a');
      const queueA = service._queues.get('type-a')!;
      const queueB = service._queues.get('type-b')!;
      expect(queueA.entries[0].arrivalTimestamp).toBe(1);
      expect(queueB.entries[0].arrivalTimestamp).toBe(2);
      expect(queueA.entries[1].arrivalTimestamp).toBe(3);
    });

    it('should freeze queue after first enqueue', () => {
      service.send('test-type');
      expect(service._queues.get('test-type')!.frozen).toBe(true);
    });

    it('should use default config when not configured', () => {
      service.send('test-type');
      const queue = service._queues.get('test-type')!;
      expect(queue.config.queueMode).toBe('accumulate');
      expect(queue.config.priority).toBe(1);
      expect(queue.config.timeoutMs).toBe(30000);
    });

    it('should throw on pending-requests overflow (1000 cap)', () => {
      // Fill up pending requests
      for (let i = 0; i < 1000; i++) {
        service.send('type-' + (i % 100).toString().padStart(3, '0'));
      }
      expect(service._pendingRequests.size).toBe(1000);
      expect(() => service.send('overflow-type')).toThrow(/Pending-requests capacity exceeded/);
    });

    it('should treat undefined payload same as empty string', () => {
      const id1 = service.send('test-type');
      const id2 = service.send('test-type', '');
      const queue = service._queues.get('test-type')!;
      const entry1 = queue.entries.find(e => e.correlationId === id1)!;
      const entry2 = queue.entries.find(e => e.correlationId === id2)!;
      expect(entry1.payload).toBe('');
      expect(entry2.payload).toBe('');
    });
  });

  describe('send() — accumulate mode', () => {
    it('should enqueue messages in FIFO order', () => {
      const id1 = service.send('test-type', 'first');
      const id2 = service.send('test-type', 'second');
      const queue = service._queues.get('test-type')!;
      expect(queue.entries[0].correlationId).toBe(id1);
      expect(queue.entries[1].correlationId).toBe(id2);
    });

    it('should discard newest when queue is at 100 capacity', () => {
      const warnSpy = jest.spyOn(console, 'warn').mockImplementation();
      for (let i = 0; i < 100; i++) {
        service.send('test-type', `msg-${i}`);
      }
      const queue = service._queues.get('test-type')!;
      expect(queue.entries.length).toBe(100);

      // 101st should be discarded
      const overflowId = service.send('test-type', 'overflow');
      expect(queue.entries.length).toBe(100);
      // Overflow message not in pending
      expect(service._pendingRequests.has(overflowId)).toBe(false);
      warnSpy.mockRestore();
    });

    it('should emit queue-overflow error on discard', () => {
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));
      jest.spyOn(console, 'warn').mockImplementation();

      for (let i = 0; i < 100; i++) {
        service.send('test-type', `msg-${i}`);
      }
      service.send('test-type', 'overflow');

      expect(errors.length).toBe(1);
      expect(errors[0].errorType).toBe('queue-overflow');
      expect(errors[0].messageType).toBe('test-type');
    });

    it('should emit warning and structured error event on queue overflow (Validates: Requirements 4.4, 13.4, 13.7, 16.1)', () => {
      const warnSpy = jest.spyOn(console, 'warn').mockImplementation();
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      // Fill accumulate queue to capacity (100 messages)
      for (let i = 0; i < 100; i++) {
        service.send('test-type', `msg-${i}`);
      }
      expect(service._queues.get('test-type')!.entries.length).toBe(100);

      // Attempt 101st message — should be discarded
      const overflowId = service.send('test-type', 'overflow-msg');

      // Assert console.warn called with overflow indication
      expect(warnSpy).toHaveBeenCalled();
      const warnCall = warnSpy.mock.calls.find(
        call => typeof call[0] === 'string' && call[0].toLowerCase().includes('overflow')
      );
      expect(warnCall).toBeDefined();

      // Assert errors$ emits structured event with correct fields
      expect(errors.length).toBe(1);
      expect(errors[0]).toEqual({
        errorType: 'queue-overflow',
        messageType: 'test-type',
        correlationId: overflowId,
        description: expect.stringContaining('test-type'),
      });

      warnSpy.mockRestore();
    });
  });

  describe('send() — latest-wins mode', () => {
    beforeEach(() => {
      service.configure('lw-type', { queueMode: 'latest-wins' });
    });

    it('should keep only the most recent message', () => {
      service.send('lw-type', 'first');
      const id2 = service.send('lw-type', 'second');
      const queue = service._queues.get('lw-type')!;
      expect(queue.entries.length).toBe(1);
      expect(queue.entries[0].correlationId).toBe(id2);
      expect(queue.entries[0].payload).toBe('second');
    });

    it('should remove old correlation ID from pending on replacement', () => {
      const id1 = service.send('lw-type', 'first');
      service.send('lw-type', 'second');
      expect(service._pendingRequests.has(id1)).toBe(false);
    });

    it('should update arrival timestamp on replacement', () => {
      service.send('lw-type', 'first');
      service.send('lw-type', 'second');
      const queue = service._queues.get('lw-type')!;
      expect(queue.entries[0].arrivalTimestamp).toBe(2);
    });
  });

  describe('cancel()', () => {
    it('should remove correlation ID from pending-requests', () => {
      const id = service.send('test-type', 'payload');
      expect(service._pendingRequests.has(id)).toBe(true);
      service.cancel(id);
      expect(service._pendingRequests.has(id)).toBe(false);
    });

    it('should not throw for unknown correlation ID', () => {
      expect(() => service.cancel('nonexistent-id')).not.toThrow();
    });
  });

  describe('dispatch', () => {
    it('should dispatch messages via bridge asynchronously (microtask)', async () => {
      service.send('test-type', 'hello');
      // Synchronously, bridge not yet called
      expect(sendMessageMock).not.toHaveBeenCalled();
      // After microtask
      await Promise.resolve();
      expect(sendMessageMock).toHaveBeenCalledTimes(1);
      expect(sendMessageMock.mock.calls[0][0]).toContain('test-type');
      expect(sendMessageMock.mock.calls[0][0]).toContain('hello');
    });

    it('should dispatch in priority order (lowest value first)', async () => {
      service.configure('high-type', { priority: 0 });
      service.configure('low-type', { priority: 2 });
      service.configure('normal-type', { priority: 1 });

      service.send('low-type', 'low');
      service.send('normal-type', 'normal');
      service.send('high-type', 'high');

      await Promise.resolve();

      expect(sendMessageMock).toHaveBeenCalledTimes(3);
      // High priority (0) dispatched first
      expect(sendMessageMock.mock.calls[0][0]).toContain('high-type');
      // Normal priority (1) second
      expect(sendMessageMock.mock.calls[1][0]).toContain('normal-type');
      // Low priority (2) last
      expect(sendMessageMock.mock.calls[2][0]).toContain('low-type');
    });

    it('should tiebreak same priority by earliest arrival timestamp', async () => {
      service.configure('type-a', { priority: 1 });
      service.configure('type-b', { priority: 1 });

      service.send('type-a', 'first');
      service.send('type-b', 'second');

      await Promise.resolve();

      expect(sendMessageMock).toHaveBeenCalledTimes(2);
      expect(sendMessageMock.mock.calls[0][0]).toContain('type-a');
      expect(sendMessageMock.mock.calls[1][0]).toContain('type-b');
    });

    it('should dispatch sequentially (one at a time)', async () => {
      service.send('test-type', 'msg1');
      service.send('test-type', 'msg2');
      service.send('test-type', 'msg3');

      await Promise.resolve();

      // All dispatched sequentially in one loop
      expect(sendMessageMock).toHaveBeenCalledTimes(3);
    });

    it('should remove entry from queue after dispatch', async () => {
      service.send('test-type', 'payload');
      const queue = service._queues.get('test-type')!;
      expect(queue.entries.length).toBe(1);

      await Promise.resolve();

      expect(queue.entries.length).toBe(0);
    });

    it('should emit bridge-error and leave pending alive on bridge failure', async () => {
      sendMessageMock.mockImplementation(() => {
        throw new Error('Bridge unavailable');
      });
      const errorSpy = jest.spyOn(console, 'error').mockImplementation();

      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      const id = service.send('test-type', 'payload');

      await Promise.resolve();

      // Error emitted
      expect(errors.length).toBe(1);
      expect(errors[0].errorType).toBe('bridge-error');
      expect(errors[0].correlationId).toBe(id);
      expect(errors[0].description).toContain('Bridge unavailable');

      // Pending entry still alive (timeout fires naturally)
      expect(service._pendingRequests.has(id)).toBe(true);

      errorSpy.mockRestore();
    });

    it('should not re-enter dispatch while already dispatching', async () => {
      // Send multiple messages — triggerDispatch called each time but only one loop runs
      service.send('test-type', 'a');
      service.send('test-type', 'b');

      await Promise.resolve();

      // Both dispatched in single loop
      expect(sendMessageMock).toHaveBeenCalledTimes(2);
      expect(service._isDispatching).toBe(false);
    });

    it('should encode envelope correctly via MessageProtocol', async () => {
      service.send('test-type', 'my-payload');

      await Promise.resolve();

      const envelope = sendMessageMock.mock.calls[0][0];
      // Format: messageType\ncorrelationId\npayload
      const parts = envelope.split('\n');
      expect(parts[0]).toBe('test-type');
      expect(parts[1]).toMatch(/^[a-zA-Z0-9-]+$/); // correlationId
      expect(parts.slice(2).join('\n')).toBe('my-payload');
    });
  });

  describe('timeout', () => {
    beforeEach(() => {
      jest.useFakeTimers();
    });

    afterEach(() => {
      jest.useRealTimers();
    });

    it('should remove pending request after timeout fires', () => {
      service.configure('test-type', { timeoutMs: 5000 });
      const id = service.send('test-type', 'payload');
      expect(service._pendingRequests.has(id)).toBe(true);

      jest.advanceTimersByTime(5000);

      expect(service._pendingRequests.has(id)).toBe(false);
    });

    it('should emit timeout error to errors$ stream', () => {
      service.configure('test-type', { timeoutMs: 5000 });
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      const id = service.send('test-type', 'payload');
      jest.advanceTimersByTime(5000);

      expect(errors.length).toBe(1);
      expect(errors[0].errorType).toBe('timeout');
      expect(errors[0].messageType).toBe('test-type');
      expect(errors[0].correlationId).toBe(id);
      expect(errors[0].description).toContain('5000ms');
    });

    it('should deliver timeout notification to subscribers', () => {
      service.configure('test-type', { timeoutMs: 5000 });
      const received: any[] = [];
      service.subscribe('test-type', msg => received.push(msg));

      const id = service.send('test-type', 'payload');
      jest.advanceTimersByTime(5000);

      expect(received.length).toBe(1);
      expect(received[0].messageType).toBe('test-type');
      expect(received[0].correlationId).toBe(id);
      expect(received[0].payload).toBe('');
    });

    it('should not fire timeout if response arrives before timeout', () => {
      service.configure('test-type', { timeoutMs: 5000 });
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      const id = service.send('test-type', 'payload');

      // Simulate inbound response before timeout
      inboundCallback!(`test-type\n${id}\nresponse-data`);

      jest.advanceTimersByTime(5000);

      // No timeout error emitted
      expect(errors.length).toBe(0);
      expect(service._pendingRequests.has(id)).toBe(false);
    });

    it('should not fire timeout if cancel() called before timeout', () => {
      service.configure('test-type', { timeoutMs: 5000 });
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      const id = service.send('test-type', 'payload');
      service.cancel(id);

      jest.advanceTimersByTime(5000);

      expect(errors.length).toBe(0);
    });

    it('should clear timeout timer on latest-wins discard', () => {
      service.configure('lw-type', { queueMode: 'latest-wins', timeoutMs: 5000 });
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      const id1 = service.send('lw-type', 'first');
      service.send('lw-type', 'second');

      // id1 timer should be cleared
      expect(service._timeoutTimers.has(id1)).toBe(false);

      jest.advanceTimersByTime(5000);

      // Only one timeout (for id2), not for id1
      const timeoutErrors = errors.filter(e => e.errorType === 'timeout');
      expect(timeoutErrors.length).toBe(1);
      expect(timeoutErrors[0].correlationId).not.toBe(id1);
    });

    it('should use default 30s timeout when not configured', () => {
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      service.send('test-type', 'payload');

      jest.advanceTimersByTime(29999);
      expect(errors.length).toBe(0);

      jest.advanceTimersByTime(1);
      expect(errors.length).toBe(1);
      expect(errors[0].errorType).toBe('timeout');
    });
  });

  describe('ngOnDestroy()', () => {
    it('should not throw on destroy', () => {
      expect(() => service.ngOnDestroy()).not.toThrow();
    });

    it('should complete errors$ stream', () => {
      let completed = false;
      service.errors$.subscribe({ complete: () => { completed = true; } });

      service.ngOnDestroy();

      expect(completed).toBe(true);
    });

    it('should clear queues', () => {
      service.send('test-type', 'payload');
      expect(service._queues.size).toBeGreaterThan(0);

      service.ngOnDestroy();

      expect(service._queues.size).toBe(0);
    });

    it('should clear pending-requests map', () => {
      service.send('test-type', 'payload');
      expect(service._pendingRequests.size).toBeGreaterThan(0);

      service.ngOnDestroy();

      expect(service._pendingRequests.size).toBe(0);
    });

    it('should clear timeout timers without firing notifications', () => {
      jest.useFakeTimers();
      service.configure('test-type', { timeoutMs: 5000 });
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      service.send('test-type', 'payload');
      expect(service._timeoutTimers.size).toBe(1);

      service.ngOnDestroy();

      expect(service._timeoutTimers.size).toBe(0);

      // Advance time — no timeout should fire
      jest.advanceTimersByTime(10000);
      // errors$ completed, so no new events
      expect(errors.length).toBe(0);

      jest.useRealTimers();
    });

    it('should clear subscribers', () => {
      service.subscribe('test-type', () => {});
      expect(service._subscribers.size).toBeGreaterThan(0);

      service.ngOnDestroy();

      expect(service._subscribers.size).toBe(0);
    });

    it('should clear fallback handler', () => {
      service.setFallbackHandler(() => {});
      expect(service._fallbackHandler).not.toBeNull();

      service.ngOnDestroy();

      expect(service._fallbackHandler).toBeNull();
    });

    it('should clear system handler', () => {
      service.setSystemHandler(() => {});
      expect(service._systemHandler).not.toBeNull();

      service.ngOnDestroy();

      expect(service._systemHandler).toBeNull();
    });
  });

  describe('inbound routing', () => {
    let receiveMessage: (raw: string) => void;

    beforeEach(() => {
      receiveMessage = inboundCallback!;
      jest.spyOn(console, 'warn').mockImplementation();
      jest.spyOn(console, 'debug').mockImplementation();
      jest.spyOn(console, 'error').mockImplementation();
    });

    afterEach(() => {
      (console.warn as jest.Mock).mockRestore();
      (console.debug as jest.Mock).mockRestore();
      (console.error as jest.Mock).mockRestore();
    });

    it('bridge error → pending stays alive → timeout fires', async () => {
      jest.useFakeTimers();
      sendMessageMock.mockImplementation(() => {
        throw new Error('Bridge down');
      });

      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      service.configure('test-type', { timeoutMs: 3000 });
      const id = service.send('test-type', 'payload');

      // Flush the dispatch microtask (Promise.resolve().then(...))
      await Promise.resolve();

      // Bridge error emitted, pending still alive
      expect(errors.some(e => e.errorType === 'bridge-error')).toBe(true);
      expect(service._pendingRequests.has(id)).toBe(true);

      // Advance to timeout
      jest.advanceTimersByTime(3000);

      // Timeout fires
      expect(errors.some(e => e.errorType === 'timeout' && e.correlationId === id)).toBe(true);
      expect(service._pendingRequests.has(id)).toBe(false);

      jest.useRealTimers();
    });

    it('subscribe returns handle with unsubscribe', () => {
      const handler = jest.fn();
      const handle = service.subscribe('test-type', handler);

      expect(handle).toBeDefined();
      expect(typeof handle.unsubscribe).toBe('function');

      // Subscriber registered
      expect(service._subscribers.get('test-type')?.has(handler)).toBe(true);

      // Unsubscribe removes it
      handle.unsubscribe();
      expect(service._subscribers.has('test-type')).toBe(false);
    });

    it('microtask delivery (not synchronous)', async () => {
      const received: any[] = [];
      service.subscribe('test-type', msg => received.push(msg));

      const id = service.send('test-type', 'payload');
      const envelope = MessageProtocol.encode('test-type', id, 'response');
      receiveMessage(envelope);

      // Synchronously: not yet delivered
      expect(received.length).toBe(0);

      // After microtask drain
      await Promise.resolve();

      expect(received.length).toBe(1);
      expect(received[0].payload).toBe('response');
    });

    it('system message prefix detection', async () => {
      const systemMsgs: any[] = [];
      service.setSystemHandler(msg => systemMsgs.push(msg));

      const envelope = MessageProtocol.encode('system:shutdown', 'abc-123', 'bye');
      receiveMessage(envelope);

      await Promise.resolve();

      expect(systemMsgs.length).toBe(1);
      expect(systemMsgs[0].messageType).toBe('system:shutdown');
    });

    it('system handler receives unsubscribed system msgs', async () => {
      const systemMsgs: any[] = [];
      service.setSystemHandler(msg => systemMsgs.push(msg));

      // No subscriber for system:alert — system handler should get it
      const envelope = MessageProtocol.encode('system:alert', 'id-001', 'warning');
      receiveMessage(envelope);

      await Promise.resolve();

      expect(systemMsgs.length).toBe(1);
      expect(systemMsgs[0].messageType).toBe('system:alert');
      expect(systemMsgs[0].payload).toBe('warning');
    });

    it('fallback receives unsubscribed non-system msgs', async () => {
      const fallbackMsgs: any[] = [];
      service.setFallbackHandler(msg => fallbackMsgs.push(msg));

      // No subscriber for 'unknown-type', not in pending — goes to fallback
      const envelope = MessageProtocol.encode('unknown-type', 'id-999', 'data');
      receiveMessage(envelope);

      await Promise.resolve();

      expect(fallbackMsgs.length).toBe(1);
      expect(fallbackMsgs[0].messageType).toBe('unknown-type');
      expect(fallbackMsgs[0].payload).toBe('data');
    });

    it('no fallback → discard + debug log', async () => {
      // No subscribers, no fallback
      const envelope = MessageProtocol.encode('orphan-type', 'id-orphan', 'lost');
      receiveMessage(envelope);

      await Promise.resolve();

      expect(console.debug).toHaveBeenCalledWith(
        expect.stringContaining('orphan-type')
      );
    });

    it('fallback error caught', async () => {
      service.setFallbackHandler(() => {
        throw new Error('Fallback exploded');
      });

      const envelope = MessageProtocol.encode('no-sub-type', 'id-fb', 'payload');
      receiveMessage(envelope);

      await Promise.resolve();

      // Error caught and logged, no unhandled exception
      expect(console.error).toHaveBeenCalledWith(
        expect.stringContaining('Fallback handler error'),
        expect.any(Error)
      );
    });

    it('fallback replaceable', async () => {
      const first: any[] = [];
      const second: any[] = [];

      service.setFallbackHandler(msg => first.push(msg));
      receiveMessage(MessageProtocol.encode('no-sub', 'id-1', 'a'));
      await Promise.resolve();

      service.setFallbackHandler(msg => second.push(msg));
      receiveMessage(MessageProtocol.encode('no-sub', 'id-2', 'b'));
      await Promise.resolve();

      expect(first.length).toBe(1);
      expect(second.length).toBe(1);
      expect(first[0].payload).toBe('a');
      expect(second[0].payload).toBe('b');
    });

    it('error stream emits structured events', () => {
      jest.useFakeTimers();
      const errors: any[] = [];
      service.errors$.subscribe(e => errors.push(e));

      service.configure('err-type', { timeoutMs: 1000 });
      const id = service.send('err-type', 'data');

      jest.advanceTimersByTime(1000);

      expect(errors.length).toBe(1);
      expect(errors[0]).toEqual({
        errorType: 'timeout',
        messageType: 'err-type',
        correlationId: id,
        description: expect.stringContaining('1000ms'),
      });

      jest.useRealTimers();
    });

    it('Backend_Push (unknown ID + subscribers) → delivered', async () => {
      const received: any[] = [];
      service.subscribe('push-type', msg => received.push(msg));

      // Unknown correlationId, but subscribers exist for the type
      const envelope = MessageProtocol.encode('push-type', 'unknown-id-xyz', 'push-data');
      receiveMessage(envelope);

      await Promise.resolve();

      expect(received.length).toBe(1);
      expect(received[0].messageType).toBe('push-type');
      expect(received[0].correlationId).toBe('unknown-id-xyz');
      expect(received[0].payload).toBe('push-data');
    });

    it('system inbound preemption', async () => {
      const deliveryOrder: string[] = [];

      service.subscribe('normal-type', msg => deliveryOrder.push('normal:' + msg.payload));
      service.setSystemHandler(msg => deliveryOrder.push('system:' + msg.payload));

      // Send a normal message first (needs to be in pending or have subscriber)
      const id = service.send('normal-type', 'req');
      // Simulate normal response arriving first
      receiveMessage(MessageProtocol.encode('normal-type', id, 'normal-resp'));
      // Then system message arrives after
      receiveMessage(MessageProtocol.encode('system:notify', 'sys-id-1', 'sys-data'));

      // Both queued before microtask fires — system should be delivered first
      await Promise.resolve();

      expect(deliveryOrder[0]).toBe('system:sys-data');
      expect(deliveryOrder[1]).toBe('normal:normal-resp');
    });
  });
});
