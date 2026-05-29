# Implementation Plan: Message Bus Service

## Overview

Implements a two-sided message bus spanning Angular frontend (Message_Bus_Client) and .NET backend (Message_Bus_Host). Replaces direct `window.external.sendMessage` / `receiveMessage` calls with a correlation-tracked, priority-dispatched, queue-managed communication layer. Wire protocol uses newline-delimited envelopes. Migrates existing open-file flow to route through the bus.

## Tasks

- [x] 1. Implement shared message protocol
  - [x] 1.1 Create MessageProtocol class (TypeScript)
    - Create `ClientApp/src/app/services/message-protocol.ts`
    - Implement `encode(messageType, correlationId, payload): string` — concatenates fields with `\n` separator
    - Implement `decode(raw): { messageType, correlationId, payload } | null` — splits on first two `\n` occurrences, returns null if fewer than 2 newlines
    - Implement `validateMessageType(type): boolean` — regex `^[a-z0-9:-]+$`, 1–64 chars
    - Implement `validateCorrelationId(id): boolean` — regex `^[a-zA-Z0-9-]+$`, 1–36 chars
    - Implement `validatePayload(payload): boolean` — length ≤ 2,097,152 chars
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.8, 8.9, 8.10, 15.1, 15.2, 15.3_

  - [x] 1.2 Create MessageProtocol class (C#)
    - Create `Services/MessageProtocol.cs`
    - Implement `Encode(messageType, correlationId, payload): string`
    - Implement `Decode(raw): (string MessageType, string CorrelationId, string Payload)?` — returns null on parse failure
    - Implement `ValidateMessageType(type): bool`
    - Implement `ValidateCorrelationId(id): bool`
    - Implement `ValidatePayload(payload): bool`
    - Same validation rules as TypeScript side
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.8, 8.9, 8.10, 15.1, 15.2, 15.3_

  - [x] 1.3 Write property test: Protocol round-trip (TypeScript)
    - **Property 1: Protocol round-trip**
    - Generate random valid Message_Type (regex `[a-z0-9:-]+`, 1–64 chars), Correlation_ID (`[a-zA-Z0-9-]+`, 1–36 chars), and payload (including newlines, unicode, 0–1000 chars)
    - Assert `decode(encode(type, id, payload))` produces values identical to inputs
    - Minimum 100 iterations
    - **Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.7**

  - [x] 1.4 Write property test: No-payload equivalence (TypeScript)
    - **Property 2: No-payload equivalence**
    - Generate random valid Message_Type and Correlation_ID
    - Assert `encode(type, id, undefined) === encode(type, id, "")`
    - Minimum 100 iterations
    - **Validates: Requirements 2.4, 8.5, 8.6**

  - [x] 1.5 Write property test: Protocol round-trip (C#)
    - **Property 1: Protocol round-trip (C#)**
    - Use FsCheck with xUnit integration
    - Generate random valid fields, assert `Decode(Encode(t, id, p)) === (t, id, p)`
    - Minimum 100 iterations
    - **Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.7**

  - [x] 1.6 Write property test: Validation rejects invalid fields (TypeScript)
    - **Property 14: Validation rejects invalid fields**
    - Generate random strings including invalid characters, oversized lengths, empty strings
    - Assert validation correctly rejects invalid and accepts valid inputs
    - Minimum 100 iterations
    - **Validates: Requirements 8.9, 8.10, 15.1, 15.2, 15.3, 15.4, 15.5, 15.6, 15.7**

  - [x] 1.7 Write property test: Validation rejects invalid fields (C#)
    - **Property 14: Validation rejects invalid fields (C#)**
    - Use FsCheck — same strategy as TypeScript side
    - Minimum 100 iterations
    - **Validates: Requirements 8.9, 8.10, 15.1, 15.2, 15.3, 15.5, 15.6**

- [x] 2. Checkpoint - Protocol layer tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [ ] 3. Implement Message_Bus_Client core (Angular)
  - [x] 3.1 Create types and interfaces
    - Create `ClientApp/src/app/services/message-bus.types.ts`
    - Define `QueueMode`, `Priority`, `MessageTypeConfig`, `BusError`, `InboundMessage`, `SubscriptionHandle`
    - Define internal types: `QueueEntry`, `MessageQueue`, `PendingRequest`
    - _Requirements: 6.1, 7.1_

  - [x] 3.2 Implement MessageBusClient service — configuration and sending
    - Create `ClientApp/src/app/services/message-bus-client.service.ts`
    - Implement as `@Injectable({ providedIn: 'root' })` singleton
    - Implement `configure(messageType, config)` — stores config, rejects invalid queue modes, rejects reconfiguration after first enqueue
    - Implement `send(messageType, payload?)` — validates fields, generates unique Correlation_ID, assigns monotonic Arrival_Timestamp, enqueues message, triggers dispatch, returns Correlation_ID
    - Implement `cancel(correlationId)` — removes from pending-requests map
    - Default config: accumulate mode, Normal priority, 30s timeout
    - Throw on validation failure, throw on pending-requests overflow (1000 cap)
    - _Requirements: 1.1, 1.3, 2.1, 2.2, 2.3, 2.4, 2.5, 6.1, 6.2, 6.5, 6.6, 12.1, 12.6, 15.4, 16.3_

  - [x] 3.3 Implement outbound queuing and priority dispatch
    - Implement accumulate-mode queue: FIFO, max 100 entries, discard newest on overflow
    - Implement latest-wins queue: max 1 entry, replace on enqueue, remove discarded Correlation_ID from pending
    - Implement priority dispatch: select queue with lowest Priority value, tiebreak by earliest Arrival_Timestamp
    - Implement sequential dispatch: one message in-flight at a time, await bridge completion before next
    - On bridge error: log error, emit to errors$, leave pending entry alive for timeout
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 5.1, 5.2, 5.3, 5.4, 7.1, 7.2, 7.3, 7.4, 7.5, 7.6, 7.7, 12.7, 13.1, 16.1, 16.2_

  - [x] 3.4 Write property test: Correlation_ID uniqueness
    - **Property 3: Correlation_ID uniqueness**
    - Generate random N (2–500), call `send()` N times with valid types
    - Assert all returned IDs are distinct
    - Minimum 100 iterations
    - **Validates: Requirements 1.3, 2.1, 2.2**

  - [x] 3.5 Write property test: Accumulate queue FIFO with bounded capacity
    - **Property 8: Accumulate queue FIFO with bounded capacity**
    - Generate random enqueue sequences (0–200 messages)
    - Assert FIFO order maintained, size never exceeds 100, overflow discards newest
    - Minimum 100 iterations
    - **Validates: Requirements 4.1, 4.2, 4.3, 4.4, 16.1**

  - [x] 3.6 Write property test: Latest-wins queue stores only newest
    - **Property 9: Latest-wins queue stores only newest**
    - Generate random enqueue sequences on a latest-wins queue
    - Assert queue contains at most 1 entry, always the most recently enqueued with correct Arrival_Timestamp
    - Minimum 100 iterations
    - **Validates: Requirements 5.1, 5.2, 16.2**

  - [x] 3.7 Write property test: Configuration immutability
    - **Property 10: Configuration immutability**
    - Generate random config + enqueue + reconfig sequences
    - Assert reconfiguration rejected after first enqueue
    - Minimum 100 iterations
    - **Validates: Requirements 6.2**

  - [x] 3.8 Write property test: Priority dispatch ordering
    - **Property 11: Priority dispatch ordering**
    - Generate random multi-priority queue states with messages
    - Assert dispatcher selects message from lowest Priority value queue, tiebreaks by earliest Arrival_Timestamp
    - Minimum 100 iterations
    - **Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5**

  - [x] 3.9 Write property test: Latest-wins discard cleans pending
    - **Property 16: Latest-wins discard cleans pending**
    - Generate random latest-wins replacement sequences
    - Assert old Correlation_ID removed from pending-requests map on replacement
    - Minimum 100 iterations
    - **Validates: Requirements 12.7**

  - [x] 3.10 Write property test: Pending-requests capacity
    - **Property 17: Pending-requests capacity**
    - Fill pending-requests to 1000 entries, attempt one more send
    - Assert throw on overflow
    - Minimum 100 iterations
    - **Validates: Requirements 16.3**

  - [x] 3.11 Write property test: Sequential outbound dispatch
    - **Property 12: Sequential outbound dispatch**
    - Generate random sequences of multiple queued messages across different types/priorities
    - Assert at most one message in-flight (transmitted to bridge) at any time — next dispatch does not begin until current completes
    - Minimum 100 iterations
    - **Validates: Requirements 7.7**

  - [x] 3.12 Write unit test: Queue overflow emits warning and error event
    - Configure accumulate queue, enqueue 100 messages, attempt 101st
    - Assert `console.warn` called with overflow indication
    - Assert `errors$` emits structured event with `errorType: 'queue-overflow'`
    - _Requirements: 4.4, 13.4, 13.7, 16.1_

- [x] 4. Checkpoint - Outbound queuing and dispatch tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 5. Implement Message_Bus_Client inbound routing
  - [x] 5.1 Implement inbound message reception and routing
    - Register `window.external.receiveMessage` callback in constructor
    - Parse inbound envelope via MessageProtocol.decode, validate fields
    - Implement two-queue inbound system: system-priority queue + normal queue
    - Route system messages (`"system:"` prefix) to system-priority queue
    - Route correlated responses (Correlation_ID in pending map) to normal queue, remove from pending
    - Route Backend_Push (unknown ID + subscribers exist) to normal queue
    - Route to fallback handler (unknown ID + no subscribers + fallback registered) to normal queue
    - Discard + debug log (unknown ID + no subscribers + no fallback)
    - Schedule microtask drain: system queue first (all entries), then normal queue
    - Discard messages failing protocol parse or field validation, log warning
    - _Requirements: 1.2, 1.10, 3.3, 3.4, 3.6, 8.8, 10.1, 10.2, 10.3, 10.4, 11.2, 11.3, 12.4, 12.5, 13.2, 15.7_

  - [x] 5.2 Implement subscription management
    - Implement `subscribe(messageType, handler)` — returns SubscriptionHandle with `unsubscribe()`
    - Deliver inbound messages to all subscribers for the Message_Type
    - Continue delivery to remaining subscribers if one throws (catch + log)
    - Implement `setFallbackHandler(handler | null)` — replaceable, catch errors
    - Implement `setSystemHandler(handler)` — always-active default system handler
    - _Requirements: 3.1, 3.2, 3.5, 10.3, 10.4, 10.5, 11.1, 11.2, 11.4, 11.5, 13.5_

  - [x] 5.3 Implement timeout and lifecycle management
    - Implement timeout checking: periodic check or per-request timer, remove expired entries, deliver timeout notification to subscriber, emit to errors$
    - Implement `ngOnDestroy()`: unregister bridge callback, complete errors$ Subject, clear queues, clear pending map, complete all internal Subjects
    - Expose `errors$: Observable<BusError>` stream
    - _Requirements: 1.5, 12.2, 12.3, 13.7_

  - [x] 5.4 Write property test: Inbound routing correctness
    - **Property 4: Inbound routing correctness**
    - Generate random inbound messages with varying subscriber/fallback configurations
    - Assert correct delivery targets per routing decision tree
    - Minimum 100 iterations
    - **Validates: Requirements 1.10, 3.3, 3.4, 11.2**

  - [x] 5.5 Write property test: Subscriber error isolation
    - **Property 5: Subscriber error isolation**
    - Generate random N subscribers, K throwers, deliver message
    - Assert all non-throwing subscribers receive the message
    - Minimum 100 iterations
    - **Validates: Requirements 3.5, 13.5**

  - [x] 5.6 Write property test: Per-type inbound delivery order
    - **Property 6: Per-type inbound delivery order**
    - Generate random sequences of inbound messages of the same type
    - Assert delivery order matches arrival order
    - Minimum 100 iterations
    - **Validates: Requirements 3.6**

  - [x] 5.7 Write property test: Unsubscribe stops delivery
    - **Property 7: Unsubscribe stops delivery**
    - Generate random subscribe/unsubscribe/message sequences
    - Assert no messages delivered after unsubscribe
    - Minimum 100 iterations
    - **Validates: Requirements 3.2**

  - [x] 5.8 Write property test: System message inbound priority
    - **Property 13: System message inbound priority**
    - Generate mixed system and non-system inbound messages arriving before microtask drain
    - Assert system messages delivered before non-system messages
    - Minimum 100 iterations
    - **Validates: Requirements 10.2**

  - [x] 5.9 Write property test: Pending-request lifecycle
    - **Property 15: Pending-request lifecycle**
    - Generate random send/response/timeout/cancel sequences
    - Assert correct pending map state transitions: response removes + delivers, timeout removes + notifies, cancel removes silently, unknown IDs handled per routing rules
    - Minimum 100 iterations
    - **Validates: Requirements 12.1, 12.3, 12.4, 12.5, 12.6**

  - [x] 5.10 Write unit tests for Message_Bus_Client
    - Test `send()` returns non-empty Correlation_ID
    - Test `send()` is non-blocking (returns immediately)
    - Test bridge error → pending stays alive → timeout fires
    - Test subscribe returns handle with unsubscribe
    - Test microtask delivery (not synchronous)
    - Test default config = accumulate + Normal
    - Test invalid queue mode rejected
    - Test default priority = Normal(1)
    - Test system message prefix detection
    - Test system handler receives unsubscribed system msgs
    - Test fallback receives unsubscribed non-system msgs
    - Test no fallback → discard + debug log
    - Test fallback error caught
    - Test fallback replaceable
    - Test timeout notification delivered
    - Test cancel removes from pending
    - Test error stream emits structured events
    - Test destroy completes streams
    - Test Backend_Push (unknown ID + subscribers) → delivered
    - Test system inbound preemption
    - _Requirements: 1.2, 1.5, 1.10, 2.2, 2.5, 2.6, 3.1, 3.2, 3.6, 6.5, 6.6, 7.6, 10.1, 10.2, 10.4, 11.1, 11.2, 11.3, 11.4, 11.5, 12.3, 12.5, 12.6, 13.7_

- [x] 6. Checkpoint - Client inbound routing and subscription tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Implement Message_Bus_Host (.NET)
  - [x] 7.1 Create MessageBusHost service
    - Create `Services/MessageBusHost.cs`
    - Implement `IDisposable`
    - Accept Photino window reference in constructor, register `WebMessageReceived` handler
    - Implement `RegisterHandler(messageType, handler)` — stores handler per message type
    - Implement sequential message processing: parse envelope, validate, find handler, await handler, encode response, send via bridge
    - On handler exception: catch, log, send `system:error` with Correlation_ID and error description
    - If no handler registered for type: discard + log warning
    - _Requirements: 1.6, 1.7, 1.8, 13.3, 14.1, 14.2, 14.4, 14.5, 15.5_

  - [x] 7.2 Implement outbound methods on MessageBusHost
    - Implement `Send(messageType, payload)` — generates Correlation_ID, validates, encodes, sends via bridge
    - Implement `SendSystemMessage(systemType, payload)` — validates `"system:"` prefix, generates ID, encodes, sends
    - Implement `SendResponse(messageType, correlationId, payload)` — validates all fields, encodes, sends
    - Throw on validation failure
    - Implement `Dispose()` — cleanup
    - _Requirements: 1.9, 10.6, 15.6_

  - [x] 7.3 Write property test: Sequential host handler processing (C#)
    - **Property 18: Sequential host handler processing**
    - Use FsCheck — generate random message sequences with async handlers
    - Assert no concurrent handler execution (track entry/exit with counter)
    - Minimum 100 iterations
    - **Validates: Requirements 14.1, 14.2, 14.4**

  - [x] 7.4 Write unit tests for MessageBusHost
    - Test handler registration + invocation
    - Test response carries same Correlation_ID
    - Test Backend_Push sends with generated ID
    - Test handler exception → system:error sent
    - Test SendSystemMessage validates prefix
    - Test unknown message type → no handler → discard + log
    - Test null handler response → no wire message sent
    - Test empty-string handler response → wire message sent
    - _Requirements: 1.7, 1.8, 1.9, 10.6, 13.3_

- [x] 8. Checkpoint - Backend host tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 9. Migrate open-file flow to Message Bus
  - [x] 9.1 Update AppComponent to use MessageBusClient for open-file
    - Inject `MessageBusClient` into `AppComponent`
    - Replace `window.external.sendMessage('open-file')` with `messageBus.send('open-file')`
    - Subscribe to "open-file" responses via `messageBus.subscribe('open-file', ...)`
    - Derive awaiting-response guard from pending-request existence
    - On non-empty response: update `displayText` signal
    - On empty response: leave `displayText` unchanged
    - Remove direct `window.external.sendMessage` and `receiveMessage` usage
    - _Requirements: 9.1, 9.2, 9.3, 9.4_

  - [x] 9.2 Update backend to register open-file handler via MessageBusHost
    - Instantiate `MessageBusHost` in `Program.cs` with the Photino window
    - Register handler for `"open-file"` message type
    - Handler invokes same native dialog flow, returns file path or empty string
    - Remove inline `WebMessageReceived` switch case for "open-file"
    - _Requirements: 9.5_

  - [x] 9.3 Write integration tests for migrated open-file flow
    - Test Ctrl+O → `messageBus.send("open-file")` → response → display updated
    - Test guard prevents duplicate sends while awaiting
    - Test empty response → display unchanged
    - Test no direct `window.external.sendMessage` calls remain in codebase
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5_

- [x] 10. Final checkpoint - All tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- TypeScript for frontend tasks, C# for backend tasks
- Frontend PBT library: fast-check (already installed, v4.5.3)
- Backend PBT library: FsCheck (xUnit integration)
- Migration is atomic — both sides update together, no backward compat needed
- Wire format: `Message_Type\nCorrelation_ID\npayload`

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2", "3.1"] },
    { "id": 1, "tasks": ["1.3", "1.4", "1.5", "1.6", "1.7"] },
    { "id": 2, "tasks": ["3.2"] },
    { "id": 3, "tasks": ["3.3"] },
    { "id": 4, "tasks": ["3.4", "3.5", "3.6", "3.7", "3.8", "3.9", "3.10", "3.11", "3.12"] },
    { "id": 5, "tasks": ["5.1"] },
    { "id": 6, "tasks": ["5.2", "5.3"] },
    { "id": 7, "tasks": ["5.4", "5.5", "5.6", "5.7", "5.8", "5.9", "5.10"] },
    { "id": 8, "tasks": ["7.1"] },
    { "id": 9, "tasks": ["7.2"] },
    { "id": 10, "tasks": ["7.3", "7.4"] },
    { "id": 11, "tasks": ["9.1", "9.2"] },
    { "id": 12, "tasks": ["9.3"] }
  ]
}
```
