import { Injectable, OnDestroy } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import {
  BusError,
  InboundMessage,
  MessageQueue,
  MessageTypeConfig,
  PendingRequest,
  QueueEntry,
  QueueMode,
  SubscriptionHandle,
} from './message-bus.types';
import { MessageProtocol } from './message-protocol';

/**
 * Message_Bus_Client — Angular singleton service for outbound queuing,
 * priority dispatch, correlation tracking, and inbound routing.
 */
@Injectable({ providedIn: 'root' })
export class MessageBusClient implements OnDestroy {
  /** Per-type queues */
  private queues = new Map<string, MessageQueue>();

  /** Correlation_ID → pending info */
  private pendingRequests = new Map<string, PendingRequest>();

  /** Correlation_ID → timeout timer handle */
  private timeoutTimers = new Map<string, ReturnType<typeof setTimeout>>();

  /** Global monotonic counter for Arrival_Timestamp */
  private arrivalCounter = 0;

  /** Error stream */
  private errorsSubject = new Subject<BusError>();
  readonly errors$: Observable<BusError> = this.errorsSubject.asObservable();

  private static readonly MAX_PENDING_REQUESTS = 1000;
  private static readonly MAX_ACCUMULATE_QUEUE_SIZE = 100;

  private static readonly DEFAULT_CONFIG: MessageTypeConfig = {
    queueMode: 'accumulate',
    priority: 1,
    timeoutMs: 30000,
  };

  // --- Inbound routing state ---

  /** System-priority inbound queue (system: prefix messages) */
  private systemInboundQueue: InboundMessage[] = [];

  /** Normal inbound queue (correlated responses, backend pushes, fallback) */
  private normalInboundQueue: InboundMessage[] = [];

  /** Per-type subscriber sets */
  private subscribers = new Map<string, Set<(msg: InboundMessage) => void>>();

  /** Fallback handler for unsubscribed non-system messages */
  private fallbackHandler: ((msg: InboundMessage) => void) | null = null;

  /** System handler for unsubscribed system messages */
  private systemHandler: ((msg: InboundMessage) => void) | null = null;

  /** Whether a microtask drain is already scheduled */
  private drainScheduled = false;

  constructor() {
    // Register inbound callback on the bridge.
    // Photino exposes receiveMessage as a function that accepts a callback.
    (window as any).external.receiveMessage((raw: string) => this.handleInbound(raw));
  }

  /**
   * Configure queue mode, priority, and timeout for a Message_Type.
   * Rejects invalid queue modes and reconfiguration after first enqueue.
   */
  configure(messageType: string, config: Partial<MessageTypeConfig>): void {
    // Validate queueMode if provided
    if (config.queueMode !== undefined) {
      if (config.queueMode !== 'accumulate' && config.queueMode !== 'latest-wins') {
        throw new Error(
          `Invalid queue mode "${config.queueMode}". Must be "accumulate" or "latest-wins".`
        );
      }
    }

    const existing = this.queues.get(messageType);
    if (existing && existing.frozen) {
      throw new Error(
        `Cannot reconfigure message type "${messageType}" after messages have been enqueued.`
      );
    }

    // Merge with existing or default config
    const baseConfig = existing?.config ?? { ...MessageBusClient.DEFAULT_CONFIG };
    const mergedConfig: MessageTypeConfig = {
      ...baseConfig,
      ...config,
    };

    if (existing) {
      existing.config = mergedConfig;
    } else {
      this.queues.set(messageType, {
        config: mergedConfig,
        entries: [],
        frozen: false,
      });
    }
  }

  /**
   * Send a message. Validates fields, generates Correlation_ID, assigns
   * monotonic Arrival_Timestamp, enqueues, triggers dispatch, returns Correlation_ID.
   */
  send(messageType: string, payload?: string): string {
    // Validate messageType
    if (!MessageProtocol.validateMessageType(messageType)) {
      throw new Error(
        `Invalid message type "${messageType}". Must match [a-z0-9:-]+, 1–64 chars.`
      );
    }

    // Validate payload if provided
    if (payload !== undefined && !MessageProtocol.validatePayload(payload)) {
      throw new Error(
        `Invalid payload. Must be at most 2,097,152 characters.`
      );
    }

    // Check pending-requests capacity
    if (this.pendingRequests.size >= MessageBusClient.MAX_PENDING_REQUESTS) {
      throw new Error(
        `Pending-requests capacity exceeded (${MessageBusClient.MAX_PENDING_REQUESTS}). Cannot send.`
      );
    }

    // Generate correlationId
    const correlationId = crypto.randomUUID();

    // Get or create queue with default config
    let queue = this.queues.get(messageType);
    if (!queue) {
      queue = {
        config: { ...MessageBusClient.DEFAULT_CONFIG },
        entries: [],
        frozen: false,
      };
      this.queues.set(messageType, queue);
    }

    // Freeze queue (no reconfig after first enqueue)
    queue.frozen = true;

    // Assign arrival timestamp
    const arrivalTimestamp = ++this.arrivalCounter;

    const entry: QueueEntry = {
      messageType,
      correlationId,
      payload: payload ?? '',
      arrivalTimestamp,
    };

    // Enqueue based on queue mode
    if (queue.config.queueMode === 'accumulate') {
      if (queue.entries.length >= MessageBusClient.MAX_ACCUMULATE_QUEUE_SIZE) {
        // Discard newest (don't add)
        console.warn(
          `Queue overflow for message type "${messageType}". Discarding newest message.`
        );
        this.errorsSubject.next({
          errorType: 'queue-overflow',
          messageType,
          correlationId,
          description: `Accumulate queue for "${messageType}" is at capacity (${MessageBusClient.MAX_ACCUMULATE_QUEUE_SIZE}). Newest message discarded.`,
        });
        // Still add to pending since correlationId was generated and returned
        // Actually per design: "discard newest = don't add if full" — message not enqueued
        // But we still need to return correlationId. The pending entry won't get a response → timeout.
        // Per task notes: "discard newest = don't add if full"
        // Don't add to pending either since message was never enqueued
        return correlationId;
      }
      queue.entries.push(entry);
    } else {
      // latest-wins: replace existing entry, remove old correlationId from pending
      if (queue.entries.length > 0) {
        const oldEntry = queue.entries[0];
        this.pendingRequests.delete(oldEntry.correlationId);
        // Clear timeout timer for discarded entry
        const oldTimer = this.timeoutTimers.get(oldEntry.correlationId);
        if (oldTimer) {
          clearTimeout(oldTimer);
          this.timeoutTimers.delete(oldEntry.correlationId);
        }
      }
      queue.entries = [entry];
    }

    // Add to pendingRequests
    this.pendingRequests.set(correlationId, {
      correlationId,
      messageType,
      enqueuedAt: Date.now(),
      timeoutMs: queue.config.timeoutMs,
    });

    // Set up per-request timeout timer
    const timeoutId = setTimeout(() => {
      if (this.pendingRequests.has(correlationId)) {
        this.pendingRequests.delete(correlationId);
        this.timeoutTimers.delete(correlationId);

        // Emit to errors$
        this.errorsSubject.next({
          errorType: 'timeout',
          messageType,
          correlationId,
          description: `Request timed out after ${queue.config.timeoutMs}ms`,
        });

        // Deliver timeout notification to subscribers
        const timeoutMsg: InboundMessage = {
          messageType,
          correlationId,
          payload: '',
        };
        this.deliverToSubscribers(timeoutMsg);
      }
    }, queue.config.timeoutMs);
    this.timeoutTimers.set(correlationId, timeoutId);

    // Trigger dispatch
    this.triggerDispatch();

    return correlationId;
  }

  /**
   * Cancel a pending request by Correlation_ID.
   * Removes from pending-requests map without notification.
   */
  cancel(correlationId: string): void {
    this.pendingRequests.delete(correlationId);
    const timer = this.timeoutTimers.get(correlationId);
    if (timer) {
      clearTimeout(timer);
      this.timeoutTimers.delete(correlationId);
    }
  }

  /** Flag: dispatch loop currently running — prevents re-entrant dispatch */
  private isDispatching = false;

  // --- Subscription management (field storage here, full logic in task 5.2) ---

  /** Subscribe to inbound messages of a given type. Returns handle w/ unsubscribe(). */
  subscribe(messageType: string, handler: (msg: InboundMessage) => void): SubscriptionHandle {
    let subs = this.subscribers.get(messageType);
    if (!subs) {
      subs = new Set();
      this.subscribers.set(messageType, subs);
    }
    subs.add(handler);

    return {
      unsubscribe: () => {
        subs!.delete(handler);
        if (subs!.size === 0) {
          this.subscribers.delete(messageType);
        }
      },
    };
  }

  /** Set fallback handler for unsubscribed non-system messages. */
  setFallbackHandler(handler: ((msg: InboundMessage) => void) | null): void {
    this.fallbackHandler = handler;
  }

  /** Set system handler for unsubscribed system messages. */
  setSystemHandler(handler: (msg: InboundMessage) => void): void {
    this.systemHandler = handler;
  }

  /** Lifecycle cleanup — unregister bridge, complete streams, clear state. */
  ngOnDestroy(): void {
    // 1. Unregister bridge callback
    // Photino's receiveMessage is a registration function — no way to unregister.
    // The callback closure will simply no-op after destroy since state is cleared below.

    // 2. Clear all timeout timers
    for (const timer of this.timeoutTimers.values()) {
      clearTimeout(timer);
    }
    this.timeoutTimers.clear();

    // 3. Complete errors$ Subject
    this.errorsSubject.complete();

    // 4. Clear queues
    this.queues.clear();
    this.systemInboundQueue.length = 0;
    this.normalInboundQueue.length = 0;

    // 5. Clear pending map (no timeout notifications for cleared entries)
    this.pendingRequests.clear();

    // 6. Clear subscribers
    this.subscribers.clear();
    this.fallbackHandler = null;
    this.systemHandler = null;
  }

  // --- Inbound routing ---

  /**
   * Handle raw inbound message from bridge.
   * Parse, validate, route to appropriate queue, schedule drain.
   */
  private handleInbound(raw: string): void {
    // 1. Parse envelope
    const decoded = MessageProtocol.decode(raw);
    if (!decoded) {
      console.warn('Inbound message discarded: protocol parse failure (fewer than 2 newlines)');
      return;
    }

    const { messageType, correlationId, payload } = decoded;

    // 2. Validate fields
    if (!MessageProtocol.validateMessageType(messageType)) {
      console.warn(`Inbound message discarded: invalid messageType "${messageType}"`);
      return;
    }
    if (!MessageProtocol.validateCorrelationId(correlationId)) {
      console.warn(`Inbound message discarded: invalid correlationId "${correlationId}"`);
      return;
    }

    const msg: InboundMessage = { messageType, correlationId, payload };

    // 3. Is system message ("system:" prefix)?
    if (messageType.startsWith('system:')) {
      this.systemInboundQueue.push(msg);
      this.scheduleDrain();
      return;
    }

    // 4. Is correlationId in pendingRequests? (correlated response)
    if (this.pendingRequests.has(correlationId)) {
      this.pendingRequests.delete(correlationId);
      // Clear timeout timer for this correlationId
      const timer = this.timeoutTimers.get(correlationId);
      if (timer) {
        clearTimeout(timer);
        this.timeoutTimers.delete(correlationId);
      }
      this.normalInboundQueue.push(msg);
      this.scheduleDrain();
      return;
    }

    // 5. Has subscribers for messageType? (Backend_Push)
    const subs = this.subscribers.get(messageType);
    if (subs && subs.size > 0) {
      this.normalInboundQueue.push(msg);
      this.scheduleDrain();
      return;
    }

    // 6. Has fallback handler?
    if (this.fallbackHandler) {
      this.normalInboundQueue.push(msg);
      this.scheduleDrain();
      return;
    }

    // 7. No subscribers, no fallback → discard + debug log
    console.debug(
      `Inbound message discarded: no subscribers or fallback for type="${messageType}" correlationId="${correlationId}"`
    );
  }

  /**
   * Schedule microtask drain if not already scheduled.
   */
  private scheduleDrain(): void {
    if (this.drainScheduled) return;
    this.drainScheduled = true;
    Promise.resolve().then(() => this.drainInbound());
  }

  /**
   * Drain inbound queues: system first (all), then normal (all).
   * Guarantees system messages delivered before normal even if they arrive after.
   */
  private drainInbound(): void {
    this.drainScheduled = false;

    // Phase 1: drain system queue completely (FIFO)
    while (this.systemInboundQueue.length > 0) {
      this.routeInbound(this.systemInboundQueue.shift()!);
    }

    // Phase 2: drain normal queue (FIFO)
    while (this.normalInboundQueue.length > 0) {
      this.routeInbound(this.normalInboundQueue.shift()!);
    }
  }

  /**
   * Deliver a message directly to subscribers for its type.
   * Used by timeout notification path (bypasses inbound queue).
   */
  private deliverToSubscribers(msg: InboundMessage): void {
    const subs = this.subscribers.get(msg.messageType);
    if (subs && subs.size > 0) {
      for (const handler of subs) {
        try {
          handler(msg);
        } catch (err) {
          console.error(`Subscriber error for "${msg.messageType}":`, err);
        }
      }
    }
  }

  /**
   * Deliver an inbound message to the appropriate handler(s).
   * Catches errors from subscribers/fallback/system handler (Req 3.5, 13.5).
   */
  private routeInbound(msg: InboundMessage): void {
    // System messages → subscribers for that type, or system handler
    if (msg.messageType.startsWith('system:')) {
      const subs = this.subscribers.get(msg.messageType);
      if (subs && subs.size > 0) {
        for (const handler of subs) {
          try {
            handler(msg);
          } catch (err) {
            console.error(`Subscriber error for "${msg.messageType}":`, err);
          }
        }
      } else if (this.systemHandler) {
        try {
          this.systemHandler(msg);
        } catch (err) {
          console.error(`System handler error for "${msg.messageType}":`, err);
        }
      }
      return;
    }

    // Non-system messages → subscribers for that type, or fallback
    const subs = this.subscribers.get(msg.messageType);
    if (subs && subs.size > 0) {
      for (const handler of subs) {
        try {
          handler(msg);
        } catch (err) {
          console.error(`Subscriber error for "${msg.messageType}":`, err);
        }
      }
    } else if (this.fallbackHandler) {
      try {
        this.fallbackHandler(msg);
      } catch (err) {
        console.error(`Fallback handler error for "${msg.messageType}":`, err);
      }
    }
  }

  // --- Internal: Outbound Dispatch ---

  /**
   * Trigger outbound dispatch asynchronously.
   * Schedules dispatch via microtask so send() returns immediately.
   * No-op if dispatch loop already running.
   */
  private triggerDispatch(): void {
    if (this.isDispatching) return;
    Promise.resolve().then(() => this.dispatchNext());
  }

  /**
   * Select next entry to dispatch using priority algorithm:
   * lowest Priority value wins; tiebreak by earliest Arrival_Timestamp.
   */
  private selectNext(): QueueEntry | null {
    let best: QueueEntry | null = null;
    let bestPriority = Infinity;
    let bestTimestamp = Infinity;

    for (const queue of this.queues.values()) {
      if (queue.entries.length === 0) continue;
      const front = queue.entries[0];
      const priority = queue.config.priority;

      if (
        priority < bestPriority ||
        (priority === bestPriority && front.arrivalTimestamp < bestTimestamp)
      ) {
        best = front;
        bestPriority = priority;
        bestTimestamp = front.arrivalTimestamp;
      }
    }
    return best;
  }

  /**
   * Remove entry from the front of its queue.
   */
  private removeFromQueue(entry: QueueEntry): void {
    const queue = this.queues.get(entry.messageType);
    if (!queue) return;
    const idx = queue.entries.indexOf(entry);
    if (idx !== -1) {
      queue.entries.splice(idx, 1);
    }
  }

  /**
   * Sequential dispatch loop: one message in-flight at a time.
   * Iterative (not recursive) to avoid stack overflow with large queues.
   */
  private dispatchNext(): void {
    this.isDispatching = true;

    let entry = this.selectNext();
    while (entry) {
      // Remove from queue front
      this.removeFromQueue(entry);

      // Encode and send via bridge
      const envelope = MessageProtocol.encode(entry.messageType, entry.correlationId, entry.payload);
      try {
        (window as any).external.sendMessage(envelope);
      } catch (err: any) {
        console.error(`Bridge error sending message type "${entry.messageType}":`, err);
        this.errorsSubject.next({
          errorType: 'bridge-error',
          messageType: entry.messageType,
          correlationId: entry.correlationId,
          description: `Bridge error: ${err?.message ?? String(err)}`,
        });
        // Leave pending entry alive — timeout fires naturally (Req 13.1)
      }

      entry = this.selectNext();
    }

    this.isDispatching = false;
  }

  // --- Test helpers (package-private access pattern) ---

  /** @internal — exposed for testing only */
  get _pendingRequests(): Map<string, PendingRequest> {
    return this.pendingRequests;
  }

  /** @internal — exposed for testing only */
  get _queues(): Map<string, MessageQueue> {
    return this.queues;
  }

  /** @internal — exposed for testing only */
  get _arrivalCounter(): number {
    return this.arrivalCounter;
  }

  /** @internal — exposed for testing only */
  get _isDispatching(): boolean {
    return this.isDispatching;
  }

  /** @internal — exposed for testing only */
  get _systemInboundQueue(): InboundMessage[] {
    return this.systemInboundQueue;
  }

  /** @internal — exposed for testing only */
  get _normalInboundQueue(): InboundMessage[] {
    return this.normalInboundQueue;
  }

  /** @internal — exposed for testing only */
  get _subscribers(): Map<string, Set<(msg: InboundMessage) => void>> {
    return this.subscribers;
  }

  /** @internal — exposed for testing only */
  get _fallbackHandler(): ((msg: InboundMessage) => void) | null {
    return this.fallbackHandler;
  }

  /** @internal — exposed for testing only */
  get _systemHandler(): ((msg: InboundMessage) => void) | null {
    return this.systemHandler;
  }

  /** @internal — exposed for testing only */
  get _drainScheduled(): boolean {
    return this.drainScheduled;
  }

  /** @internal — exposed for testing only */
  get _timeoutTimers(): Map<string, ReturnType<typeof setTimeout>> {
    return this.timeoutTimers;
  }
}
