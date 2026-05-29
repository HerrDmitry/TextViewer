/**
 * Message Bus types and interfaces.
 * Defines the public API types and internal state structures for Message_Bus_Client.
 */

// --- Public Types ---

export type QueueMode = 'accumulate' | 'latest-wins';
export type Priority = 0 | 1 | 2; // High=0, Normal=1, Low=2

export interface MessageTypeConfig {
  queueMode: QueueMode;
  priority: Priority;
  timeoutMs: number; // default 30000
}

export interface BusError {
  errorType: 'bridge-error' | 'validation-error' | 'timeout' | 'capacity-overflow' | 'queue-overflow';
  messageType: string;
  correlationId: string;
  description: string;
}

export interface InboundMessage {
  messageType: string;
  correlationId: string;
  payload: string;
}

export interface SubscriptionHandle {
  unsubscribe(): void;
}

// --- Internal Types ---

export interface QueueEntry {
  messageType: string;
  correlationId: string;
  payload: string;
  arrivalTimestamp: number; // monotonic counter — GLOBAL across all Message_Types
}

export interface MessageQueue {
  config: MessageTypeConfig;
  entries: QueueEntry[]; // accumulate: FIFO array; latest-wins: 0 or 1 entry
  frozen: boolean; // true after first enqueue → no reconfig
}

export interface PendingRequest {
  correlationId: string;
  messageType: string;
  enqueuedAt: number; // Date.now() for timeout calc
  timeoutMs: number;
}
