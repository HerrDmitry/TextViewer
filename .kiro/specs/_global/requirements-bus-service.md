# Message Bus Service — Requirements

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

Message Bus Service — a two-sided communication layer spanning both the Angular frontend and .NET backend. All inter-layer messaging routes through this service instead of direct `window.external.sendMessage` / `receiveMessage` calls. Either side can initiate messages: the frontend sends requests with unique correlation IDs and receives correlated responses, and the backend can push unsolicited messages to the frontend. Supports multiple message types, queuing of same-type messages, a "latest wins" mode that discards queued messages of the same type when a newer one arrives, and priority-based dispatch with arrival-order tiebreaking across queues.

## Glossary

- **Message_Bus**: The combined communication service spanning both Angular frontend (Message_Bus_Client) and .NET backend (Message_Bus_Host)
- **Message_Bus_Client**: The Angular injectable singleton service — queues outbound messages, dispatches them to the bridge, and routes inbound responses to callers
- **Message_Bus_Host**: The .NET backend service — receives messages from the bridge, routes them to registered handlers, sends responses back, and can initiate unsolicited messages to the frontend
- **Message_Type**: A string identifier categorizing a message (e.g. `"open-file"`, `"file-content"`)
- **Correlation_ID**: A unique string identifier generated per outbound request, used to match a response back to the originating request
- **Message_Queue**: An ordered collection of pending outbound messages of a given Message_Type
- **Queue_Mode**: The queuing strategy for a Message_Type — either "accumulate" (queue all) or "latest-wins" (discard older, keep newest)
- **Outbound_Message**: A message sent from one side to the other via Message_Bridge, containing Message_Type, Correlation_ID, and optional payload
- **Inbound_Message**: A message received from the other side via Message_Bridge, containing Message_Type, Correlation_ID, and optional payload
- **Backend_Push**: An unsolicited message initiated by Message_Bus_Host and sent to Message_Bus_Client without a prior frontend request
- **System_Message**: A message with a reserved Message_Type prefix `"system:"` — always processed at High priority regardless of configuration, delivered even without explicit Subscribers
- **Fallback_Handler**: A single catch-all handler registered on Message_Bus_Client that receives any Inbound_Message whose Message_Type has no registered Subscribers and is not a System_Message
- **Subscriber**: A component or service registered to receive Inbound_Messages of a specific Message_Type
- **Handler**: A .NET function registered on Message_Bus_Host to process incoming messages of a specific Message_Type
- **Priority**: A predefined level assigned per Message_Type determining dispatch order. Three levels: High (0), Normal (1), Low (2)
- **Arrival_Timestamp**: A monotonically increasing sequence number assigned to each outbound message when it enters the Message_Bus_Client
- **Dispatch_Order**: The sequence in which queued outbound messages are transmitted to the Message_Bridge, determined first by Priority then by Arrival_Timestamp

## Requirements

### Requirement 1: Service Registration and Lifecycle

**User Story:** As a developer, I want a two-sided message bus (frontend client + backend host), so that communication is centralized, correlation-tracked, and testable on both sides.

#### Acceptance Criteria

1. THE Message_Bus_Client SHALL be provided as a singleton Angular injectable service available application-wide
2. WHEN the Message_Bus_Client is instantiated, THE Message_Bus_Client SHALL register a callback via the Message_Bridge inbound channel (`window.external.receiveMessage`) to receive all backend-originated messages
3. THE Message_Bus_Client SHALL expose a method to send a request with a Message_Type and optional payload, which generates a unique Correlation_ID and returns it to the caller
4. THE Message_Bus_Client SHALL expose a method to subscribe to responses by Message_Type, delivering the Correlation_ID and payload to the subscriber when a matching response arrives
5. WHEN the Application is destroyed, THE Message_Bus_Client SHALL complete all exposed observable streams so that active subscribers receive a completion notification
6. THE Message_Bus_Host SHALL be a .NET service instantiated at application startup that registers a `WebMessageReceived` handler on the Photino_Window via IMessageBridge
7. THE Message_Bus_Host SHALL allow registering Handler functions per Message_Type to process incoming requests
8. WHEN the Message_Bus_Host receives a message from the bridge, THE Message_Bus_Host SHALL parse the Message_Type and Correlation_ID, invoke the registered Handler, and send the response back via `SendWebMessage` with the same Correlation_ID
9. THE Message_Bus_Host SHALL expose a method to send unsolicited messages (Backend_Push) to the frontend with a Message_Type, a backend-generated Correlation_ID, and optional payload
10. WHEN the Message_Bus_Client receives a Backend_Push (a message whose Correlation_ID does not match any pending frontend request), THE Message_Bus_Client SHALL deliver it to Subscribers registered for that Message_Type

### Requirement 2: Sending Messages (Frontend → Backend)

**User Story:** As a developer, I want to send typed messages with correlation IDs to the backend through the Message_Bus_Client, so that responses can be matched to their originating requests.

#### Acceptance Criteria

1. WHEN a component calls the send method with a Message_Type and optional payload, THE Message_Bus_Client SHALL generate a unique Correlation_ID, encode the message, and enqueue it for dispatch
2. THE Message_Bus_Client SHALL return the generated Correlation_ID to the caller
3. THE Message_Bus_Client SHALL support sending messages with a string payload or with no payload (command-only)
4. THE Message_Bus_Client SHALL treat no-payload and empty-string payload as equivalent on the wire
5. THE Message_Bus_Client send method SHALL be non-blocking — returns immediately without waiting for backend response
6. IF the Message_Bridge throws an error during transmission, THEN THE Message_Bus_Client SHALL catch the error, leave the Correlation_ID in pending-requests (timeout fires naturally)
7. THE Message_Bus_Client SHALL only discard messages due to Message_Bridge transmission errors — other error conditions follow their own defined policies

### Requirement 3: Receiving Responses (Backend → Frontend)

**User Story:** As a developer, I want to subscribe to typed responses and have them correlated to my original request.

#### Acceptance Criteria

1. THE Message_Bus_Client SHALL allow Subscribers to register for responses by Message_Type and return a subscription handle
2. WHEN a Subscriber unregisters, THE Message_Bus_Client SHALL stop delivering responses of that Message_Type to that Subscriber
3. WHEN an Inbound_Message arrives, THE Message_Bus_Client SHALL deliver the payload along with the Correlation_ID to all Subscribers registered for that Message_Type
4. WHEN an Inbound_Message arrives with no registered Subscribers and is not a System_Message, route to Fallback_Handler or discard
5. IF a Subscriber throws an error, continue delivering to remaining Subscribers
6. Deliver Inbound_Messages asynchronously via microtask scheduling; per-type delivery order preserved (FIFO)

### Requirement 4: Message Queuing — Accumulate Mode

#### Acceptance Criteria

1. Enqueue in FIFO order with Arrival_Timestamp
2. Dispatch transmits oldest (front) message
3. Preserve all enqueued messages up to 100 per type
4. At 100 messages: discard newest incoming message

### Requirement 5: Message Queuing — Latest-Wins Mode

#### Acceptance Criteria

1. Retain only the most recently enqueued message, replacing any previous
2. Atomically discard previous, store new, update Arrival_Timestamp
3. Dispatch sends the single stored message
4. Empty queue → no transmission, no error

### Requirement 6: Queue Mode Configuration

#### Acceptance Criteria

1. Two modes: "accumulate" and "latest-wins"
2. Config per Message_Type at registration time only — no reconfig after first enqueue
3. Default: "accumulate" mode, Normal priority, 30s timeout
4. Invalid queue mode → reject with error

### Requirement 7: Priority-Based Dispatch

#### Acceptance Criteria

1. Three levels: High (0), Normal (1), Low (2)
2. Monotonically increasing Arrival_Timestamp per message
3. Dispatch selects lowest Priority value queue
4. Same priority → earliest Arrival_Timestamp wins
5. Latest-wins uses most recent message's timestamp for ordering
6. Default priority: Normal (1)
7. Sequential dispatch — one in-flight at a time

### Requirement 8: Message Protocol Format

#### Acceptance Criteria

1. Wire envelope: `Message_Type\nCorrelation_ID\npayload`
2. Fields in order: type, id, payload (payload may contain newlines)
3. Encode: concatenate with `\n` separators
4. Decode: split on first two `\n` occurrences
5. No payload → trailing newline, empty payload
6. Empty-string payload identical to no-payload on wire
7. Round-trip preserves payload for valid inputs ≤ 2MB
8. < 2 newlines → discard
9. Correlation_ID: `[a-zA-Z0-9-]+`, max 36 chars
10. Message_Type: `[a-z0-9:-]+`, max 64 chars

### Requirement 9: System Messages

#### Acceptance Criteria

1. Reserved prefix `"system:"` for all System_Messages
2. System messages delivered before non-system (inbound priority)
3. Delivered to subscribers for that system type
4. No subscribers → delivered to dedicated system handler (not fallback)
5. Method to register default system handler
6. Host exposes method to send system messages

### Requirement 10: Fallback Handler

#### Acceptance Criteria

1. Exactly one Fallback_Handler via dedicated method
2. Receives unsubscribed non-system messages
3. No fallback + no subscribers → discard + debug log
4. Fallback errors caught
5. Replaceable/unregisterable at any time

### Requirement 11: Request Lifecycle and Timeout

#### Acceptance Criteria

1. Pending-requests map tracks Correlation_ID + timestamp
2. Configurable timeout per type (default 30s)
3. Timeout → remove from pending, notify subscriber, emit to errors$
4. Response → remove from pending, deliver to subscribers
5. Unknown Correlation_ID (not pending, not Backend_Push) → discard
6. Cancel method removes from pending silently
7. Latest-wins discard → remove from pending (never sent)

### Requirement 12: Error Policy

#### Acceptance Criteria

1. Bridge error → discard, log, emit error event, leave pending alive for timeout
2. Parse failure → discard, log warning
3. Host handler exception → catch, log, send `system:error` with correlationId
4. Queue overflow → discard newest, log warning
5. Subscriber/fallback error → catch, log, continue
6. No automatic retry
7. Observable error stream for monitoring

### Requirement 13: Host Handler Concurrency

#### Acceptance Criteria

1. Sequential processing in arrival order — no concurrent handlers
2. Await handler completion before next message
3. Long-running work may offload to background
4. Same-type ordering guaranteed
5. No cross-type ordering beyond sequential processing

### Requirement 14: Message Validation

#### Acceptance Criteria

1. Message_Type: `[a-z0-9:-]+`, max 64 chars
2. Correlation_ID: `[a-zA-Z0-9-]+`, max 36 chars
3. Payload: max 2,097,152 chars
4. Client outbound: throw on invalid
5. Host inbound: discard + warn on invalid
6. Host outbound: throw on invalid
7. Client inbound: discard + warn on invalid

### Requirement 15: Capacity Constraints

#### Acceptance Criteria

1. Accumulate queue: max 100 per type
2. Latest-wins queue: max 1 per type
3. Pending-requests map: max 1000 global — throw on overflow
4. Error stream: no capacity limit
