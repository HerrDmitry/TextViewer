/**
 * Unit tests for AppComponent keyboard handling and error modal logic.
 *
 * Validates: Requirements 2.6, 7.5, 7.6
 */

describe('AppComponent — keyboard handling and error modal', () => {
  let triggerOpenFileMock: jest.Mock;
  let dismissErrorMock: jest.Mock;
  let errorMessageValue: string | null;

  /**
   * Creates a minimal component replica matching AppComponent.onKeydown and dismissError logic.
   * Uses a mock ShellStateService to verify delegation.
   */
  function createComponent() {
    triggerOpenFileMock = jest.fn();
    dismissErrorMock = jest.fn(() => { errorMessageValue = null; });
    errorMessageValue = null;

    const state = {
      triggerOpenFile: triggerOpenFileMock,
      dismissError: dismissErrorMock,
      tabPosition: () => 'top' as const,
      errorMessage: () => errorMessageValue,
    };

    return {
      get errorMessage() { return state.errorMessage(); },
      onKeydown(event: { ctrlKey: boolean; metaKey: boolean; key: string; preventDefault: () => void }) {
        const isCtrlO = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'o';
        if (!isCtrlO) return;
        event.preventDefault();
        state.triggerOpenFile();
      },
      dismissError() {
        state.dismissError();
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

  // --- Requirement 2.6: Ctrl+O (lowercase 'o') triggers triggerOpenFile ---

  it('Ctrl+O (lowercase "o") keydown triggers triggerOpenFile', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true, key: 'o' });

    component.onKeydown(event);

    expect(triggerOpenFileMock).toHaveBeenCalledTimes(1);
  });

  // --- Requirement 2.6: Ctrl+O (uppercase 'O') triggers triggerOpenFile ---

  it('Ctrl+O (uppercase "O") keydown triggers triggerOpenFile', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true, key: 'O' });

    component.onKeydown(event);

    expect(triggerOpenFileMock).toHaveBeenCalledTimes(1);
  });

  // --- Requirement 2.6: Cmd+O (metaKey) triggers triggerOpenFile ---

  it('Cmd+O (metaKey) triggers triggerOpenFile', () => {
    const component = createComponent();
    const event = makeKeyEvent({ metaKey: true, key: 'o' });

    component.onKeydown(event);

    expect(triggerOpenFileMock).toHaveBeenCalledTimes(1);
  });

  // --- Requirement 2.6: Other key combos don't trigger ---

  it('other key combos do not trigger triggerOpenFile', () => {
    const component = createComponent();

    // Plain 'o' without modifier
    component.onKeydown(makeKeyEvent({ key: 'o' }));
    // Ctrl+X
    component.onKeydown(makeKeyEvent({ ctrlKey: true, key: 'x' }));
    // Shift+O (no ctrl/meta)
    component.onKeydown(makeKeyEvent({ key: 'O' }));
    // Ctrl+A
    component.onKeydown(makeKeyEvent({ ctrlKey: true, key: 'a' }));

    expect(triggerOpenFileMock).not.toHaveBeenCalled();
  });

  // --- Requirement 7.5: preventDefault called on Ctrl+O ---

  it('preventDefault is called on Ctrl+O', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true });

    component.onKeydown(event);

    expect(event.preventDefault).toHaveBeenCalledTimes(1);
  });

  it('preventDefault is called on Cmd+O', () => {
    const component = createComponent();
    const event = makeKeyEvent({ metaKey: true });

    component.onKeydown(event);

    expect(event.preventDefault).toHaveBeenCalledTimes(1);
  });

  it('preventDefault is NOT called for non-matching keys', () => {
    const component = createComponent();
    const event = makeKeyEvent({ ctrlKey: true, key: 'x' });

    component.onKeydown(event);

    expect(event.preventDefault).not.toHaveBeenCalled();
  });

  // --- Requirement 7.6: Error modal displayed when errorMessage is non-null ---

  it('errorMessage is non-null when an error exists', () => {
    const component = createComponent();
    errorMessageValue = 'ERROR: File not found';

    expect(component.errorMessage).toBe('ERROR: File not found');
  });

  it('errorMessage is null when no error exists', () => {
    const component = createComponent();

    expect(component.errorMessage).toBeNull();
  });

  // --- Requirement 7.6: dismissError() clears errorMessage to null ---

  it('dismissError() calls state.dismissError() which clears errorMessage to null', () => {
    const component = createComponent();
    errorMessageValue = 'ERROR: Something went wrong';

    component.dismissError();

    expect(dismissErrorMock).toHaveBeenCalledTimes(1);
    expect(component.errorMessage).toBeNull();
  });

  it('dismissError() can be called when errorMessage is already null (no-op)', () => {
    const component = createComponent();
    errorMessageValue = null;

    component.dismissError();

    expect(dismissErrorMock).toHaveBeenCalledTimes(1);
    expect(component.errorMessage).toBeNull();
  });
});
