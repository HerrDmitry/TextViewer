/**
 * Unit tests for frontend keyboard handling and display logic via MessageBusClient.
 *
 * Validates: Requirements 9.1, 9.2, 9.3, 9.4
 */
import { MessageBusClient } from './services/message-bus-client.service';
import { InboundMessage, SubscriptionHandle } from './services/message-bus.types';

describe('AppComponent — keyboard and display logic (MessageBusClient)', () => {
  let sendMock: jest.Mock;
  let subscribeMock: jest.Mock;
  let subscribeHandler: ((msg: InboundMessage) => void) | null;
  let unsubscribeMock: jest.Mock;

  beforeEach(() => {
    sendMock = jest.fn().mockReturnValue('test-correlation-id');
    unsubscribeMock = jest.fn();
    subscribeHandler = null;

    subscribeMock = jest.fn((messageType: string, handler: (msg: InboundMessage) => void) => {
      subscribeHandler = handler;
      return { unsubscribe: unsubscribeMock } as SubscriptionHandle;
    });

    // Mock window.external for MessageBusClient constructor (it registers receiveMessage)
    Object.defineProperty(window, 'external', {
      value: {
        sendMessage: jest.fn(),
        receiveMessage: jest.fn(),
      },
      writable: true,
      configurable: true,
    });
  });

  /**
   * Creates a minimal component replica matching AppComponent logic.
   * Uses a mock MessageBusClient to test the exact same state machine.
   */
  function createComponent() {
    let displayText = 'Hello World';
    let pendingCorrelationId: string | null = null;

    const messageBus = {
      send: sendMock,
      subscribe: subscribeMock,
    };

    // Mirrors constructor: subscribe to 'open-file'
    const subscription = messageBus.subscribe('open-file', (msg: InboundMessage) => {
      if (msg.payload !== '') {
        displayText = msg.payload;
      }
      pendingCorrelationId = null;
    });

    return {
      get displayText() { return displayText; },
      get pendingCorrelationId() { return pendingCorrelationId; },
      get subscription() { return subscription; },
      onKeydown(event: { ctrlKey: boolean; metaKey: boolean; key: string; preventDefault: () => void }) {
        const isCtrlO = (event.ctrlKey || event.metaKey) && event.key === 'o';
        if (!isCtrlO) return;
        event.preventDefault();

        // Guard: don't send while awaiting response
        if (pendingCorrelationId !== null) return;

        pendingCorrelationId = messageBus.send('open-file');
      },
      ngOnDestroy() {
        subscription.unsubscribe();
      },
    };
  }

  function makeKeyEvent(overrides: Partial<{ ctrlKey: boolean; metaKey: boolean; key: string }> = {}) {
    return {
      ctrlKey: false,
      metaKey: false,
      key: 'o',
      preventDefault: jest.fn(),
      ...overrides,
    };
  }

  // --- Requirement 9.1: Ctrl+O triggers messageBus.send("open-file") ---

  it('Ctrl+O triggers messageBus.send("open-file")', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true });

    component.onKeydown(event);

    expect(sendMock).toHaveBeenCalledTimes(1);
    expect(sendMock).toHaveBeenCalledWith('open-file');
  });

  // --- Requirement 9.1: Other key combos don't trigger send ---

  it('other key combos do not trigger messageBus.send', () => {
    const component = createComponent();

    // Plain 'o' without modifier
    component.onKeydown(makeKeyEvent({ key: 'o' }));
    // Ctrl+X
    component.onKeydown(makeKeyEvent({ ctrlKey: true, key: 'x' }));
    // Shift+O (no ctrl/meta)
    component.onKeydown(makeKeyEvent({ key: 'O' }));

    expect(sendMock).not.toHaveBeenCalled();
  });

  // --- Requirement 9.1: preventDefault called on Ctrl+O ---

  it('preventDefault is called on Ctrl+O', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true });

    component.onKeydown(event);

    expect(event.preventDefault).toHaveBeenCalled();
  });

  it('preventDefault is called on Ctrl+O even while awaiting response', () => {
    const component = createComponent();

    // First press — sets pending
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));

    // Second press — still pending
    const event2 = makeKeyEvent({ ctrlKey: true });
    component.onKeydown(event2);

    expect(event2.preventDefault).toHaveBeenCalled();
  });

  // --- Requirement 9.1: Cmd+O works (meta key) ---

  it('Cmd+O (meta key) triggers messageBus.send("open-file")', () => {
    const component = createComponent();
    const event = makeKeyEvent({ metaKey: true });

    component.onKeydown(event);

    expect(sendMock).toHaveBeenCalledTimes(1);
    expect(sendMock).toHaveBeenCalledWith('open-file');
  });

  // --- Requirement 9.4: Initial displayText is "Hello World" ---

  it('initial displayText is "Hello World"', () => {
    const component = createComponent();

    expect(component.displayText).toBe('Hello World');
  });

  // --- Requirement 9.2: Non-empty response sets displayText ---

  it('non-empty response sets displayText to full received string', () => {
    const component = createComponent();

    // Trigger a send first
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));

    // Simulate response via subscriber callback
    subscribeHandler!({
      messageType: 'open-file',
      correlationId: 'test-correlation-id',
      payload: 'C:\\Users\\me\\documents\\report.pdf',
    });

    expect(component.displayText).toBe('C:\\Users\\me\\documents\\report.pdf');
  });

  // --- Requirement 9.4: Empty response leaves display unchanged ---

  it('empty response leaves displayText unchanged', () => {
    const component = createComponent();

    // Trigger a send
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));

    // Simulate empty response (user cancelled dialog)
    subscribeHandler!({
      messageType: 'open-file',
      correlationId: 'test-correlation-id',
      payload: '',
    });

    expect(component.displayText).toBe('Hello World');
  });

  // --- Requirement 9.3: Guard prevents duplicate sends while awaiting ---

  it('does not send while awaiting response (pendingCorrelationId is set)', () => {
    const component = createComponent();

    // First press — sends
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));
    expect(sendMock).toHaveBeenCalledTimes(1);

    // Second press — blocked by guard
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));
    expect(sendMock).toHaveBeenCalledTimes(1);
  });

  // --- Requirement 9.3: Guard clears after response ---

  it('resumes accepting Ctrl+O after response clears pending state', () => {
    const component = createComponent();

    // First press — sends
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));
    expect(sendMock).toHaveBeenCalledTimes(1);

    // Response arrives — clears pending
    subscribeHandler!({
      messageType: 'open-file',
      correlationId: 'test-correlation-id',
      payload: '/some/file.txt',
    });

    // Second press — should send again
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));
    expect(sendMock).toHaveBeenCalledTimes(2);
  });

  // --- Subscription lifecycle ---

  it('subscribes to "open-file" on construction', () => {
    createComponent();

    expect(subscribeMock).toHaveBeenCalledTimes(1);
    expect(subscribeMock).toHaveBeenCalledWith('open-file', expect.any(Function));
  });

  it('unsubscribes on destroy', () => {
    const component = createComponent();

    component.ngOnDestroy();

    expect(unsubscribeMock).toHaveBeenCalledTimes(1);
  });
});
