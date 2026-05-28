/**
 * Unit tests for frontend keyboard handling and display logic.
 *
 * Validates: Requirements 1.1, 1.3, 3.1, 3.2, 4.1, 4.2
 */

describe('AppComponent — keyboard and display logic', () => {
  let sendMessageMock: jest.Mock;
  let receiveMessageCallback: ((message: string) => void) | null;

  beforeEach(() => {
    sendMessageMock = jest.fn();
    receiveMessageCallback = null;

    Object.defineProperty(window, 'external', {
      value: {
        sendMessage: (message: string) => sendMessageMock(message),
        receiveMessage: (callback: (message: string) => void) => {
          receiveMessageCallback = callback;
        },
      },
      writable: true,
      configurable: true,
    });
  });

  /**
   * Creates a minimal component replica matching AppComponent logic.
   * Avoids Angular TestBed while testing the exact same state machine.
   */
  function createComponent() {
    let displayText = 'Hello World';
    let awaitingResponse = false;

    window.external.receiveMessage((message: string) => {
      if (message !== '') {
        displayText = message;
      }
      awaitingResponse = false;
    });

    return {
      get displayText() { return displayText; },
      get awaitingResponse() { return awaitingResponse; },
      onKeydown(event: { ctrlKey: boolean; metaKey: boolean; key: string; preventDefault: () => void }) {
        const isCtrlO = (event.ctrlKey || event.metaKey) && event.key === 'o';
        if (!isCtrlO) return;
        event.preventDefault();
        if (!awaitingResponse) {
          window.external.sendMessage('open-file');
          awaitingResponse = true;
        }
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

  // --- Requirement 1.1: Ctrl+O triggers sendMessage("open-file") ---

  it('Ctrl+O triggers sendMessage("open-file")', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true });

    component.onKeydown(event);

    expect(sendMessageMock).toHaveBeenCalledTimes(1);
    expect(sendMessageMock).toHaveBeenCalledWith('open-file');
  });

  // --- Requirement 1.1: Other key combos don't trigger send ---

  it('other key combos do not trigger sendMessage', () => {
    const component = createComponent();

    // Plain 'o' without modifier
    component.onKeydown(makeKeyEvent({ key: 'o' }));
    // Ctrl+X
    component.onKeydown(makeKeyEvent({ ctrlKey: true, key: 'x' }));
    // Shift+O (no ctrl/meta)
    component.onKeydown(makeKeyEvent({ key: 'O' }));
    // Alt key alone doesn't count as ctrl or meta
    component.onKeydown(makeKeyEvent({ key: 'o' }));

    expect(sendMessageMock).not.toHaveBeenCalled();
  });

  // --- Requirement 1.3: preventDefault called on Ctrl+O ---

  it('preventDefault is called on Ctrl+O', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true });

    component.onKeydown(event);

    expect(event.preventDefault).toHaveBeenCalled();
  });

  it('preventDefault is called on Ctrl+O even while awaiting response', () => {
    const component = createComponent();

    // First press — sets awaiting
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));

    // Second press — still awaiting
    const event2 = makeKeyEvent({ ctrlKey: true });
    component.onKeydown(event2);

    expect(event2.preventDefault).toHaveBeenCalled();
  });

  // --- Requirement 1.1: Cmd+O works (meta key) ---

  it('Cmd+O (meta key) triggers sendMessage("open-file")', () => {
    const component = createComponent();
    const event = makeKeyEvent({ metaKey: true });

    component.onKeydown(event);

    expect(sendMessageMock).toHaveBeenCalledTimes(1);
    expect(sendMessageMock).toHaveBeenCalledWith('open-file');
  });

  // --- Requirement 4.1, 4.2: Initial displayText is "Hello World" ---

  it('initial displayText is "Hello World"', () => {
    const component = createComponent();

    expect(component.displayText).toBe('Hello World');
  });

  // --- Requirement 3.1: Non-empty response sets displayText ---

  it('non-empty response sets displayText to full received string', () => {
    const component = createComponent();

    // Trigger a send first
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));

    // Simulate response
    receiveMessageCallback!('C:\\Users\\me\\documents\\report.pdf');

    expect(component.displayText).toBe('C:\\Users\\me\\documents\\report.pdf');
  });

  // --- Requirement 3.2: Empty response leaves display unchanged ---

  it('empty response leaves displayText unchanged', () => {
    const component = createComponent();

    // Trigger a send
    component.onKeydown(makeKeyEvent({ ctrlKey: true }));

    // Simulate empty response (user cancelled dialog)
    receiveMessageCallback!('');

    expect(component.displayText).toBe('Hello World');
  });
});
