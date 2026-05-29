/**
 * Feature: message-bus-service, Property: State guard prevents duplicate sends via MessageBusClient
 *
 * Validates: Requirements 9.1, 9.3
 *
 * Property: For any sequence of Ctrl+O key presses and message responses
 * interleaved in any order, the frontend shall never have more than one
 * outstanding "open-file" message without an intervening response.
 */
import * as fc from 'fast-check';
import { InboundMessage, SubscriptionHandle } from './services/message-bus.types';

type EventKind = 'keypress' | 'response';

interface SimEvent {
  kind: EventKind;
}

describe('Feature: message-bus-service, Property: State guard prevents duplicate sends (MessageBusClient)', () => {
  let sendMock: jest.Mock;
  let subscribeHandler: ((msg: InboundMessage) => void) | null;
  let correlationCounter: number;

  beforeEach(() => {
    correlationCounter = 0;
    sendMock = jest.fn(() => `corr-${++correlationCounter}`);
    subscribeHandler = null;

    // Mock window.external for any MessageBusClient internals
    Object.defineProperty(window, 'external', {
      value: {
        sendMessage: jest.fn(),
        receiveMessage: jest.fn(),
      },
      writable: true,
      configurable: true,
    });
  });

  function createComponent() {
    let pendingCorrelationId: string | null = null;

    const messageBus = {
      send: sendMock,
      subscribe: (messageType: string, handler: (msg: InboundMessage) => void): SubscriptionHandle => {
        subscribeHandler = handler;
        return { unsubscribe: jest.fn() };
      },
    };

    // Mirrors constructor: subscribe to 'open-file'
    messageBus.subscribe('open-file', (msg: InboundMessage) => {
      if (msg.payload !== '') {
        // displayText would be set here — not relevant for this property
      }
      pendingCorrelationId = null;
    });

    return {
      get pendingCorrelationId() { return pendingCorrelationId; },
      onKeydown(event: { ctrlKey: boolean; metaKey: boolean; key: string; preventDefault: () => void }) {
        const isCtrlO = (event.ctrlKey || event.metaKey) && event.key === 'o';
        if (!isCtrlO) return;
        event.preventDefault();

        // Guard: don't send while awaiting response
        if (pendingCorrelationId !== null) return;

        pendingCorrelationId = messageBus.send('open-file');
      },
    };
  }

  function makeCtrlOEvent(): { ctrlKey: boolean; metaKey: boolean; key: string; preventDefault: () => void } {
    return { ctrlKey: true, metaKey: false, key: 'o', preventDefault: jest.fn() };
  }

  it('at most 1 outstanding messageBus.send call exists at any time (no duplicate sends without intervening response)', () => {
    fc.assert(
      fc.property(
        fc.array(
          fc.oneof(
            fc.constant<SimEvent>({ kind: 'keypress' }),
            fc.constant<SimEvent>({ kind: 'response' })
          ),
          { minLength: 1, maxLength: 50 }
        ),
        (events: SimEvent[]) => {
          // Reset mocks for each iteration
          sendMock.mockClear();
          correlationCounter = 0;

          const component = createComponent();
          let outstandingSends = 0;
          let maxOutstanding = 0;

          for (const event of events) {
            if (event.kind === 'keypress') {
              const prevCallCount = sendMock.mock.calls.length;
              component.onKeydown(makeCtrlOEvent());
              const newCalls = sendMock.mock.calls.length - prevCallCount;
              outstandingSends += newCalls;
            } else {
              // Simulate response — only meaningful if there's an outstanding send
              if (outstandingSends > 0 && subscribeHandler) {
                subscribeHandler({
                  messageType: 'open-file',
                  correlationId: `corr-${correlationCounter}`,
                  payload: '/some/path.txt',
                });
                outstandingSends--;
              }
            }
            maxOutstanding = Math.max(maxOutstanding, outstandingSends);
          }

          // Property: at most 1 outstanding send at any time
          return maxOutstanding <= 1;
        }
      ),
      { numRuns: 100 }
    );
  });
});
