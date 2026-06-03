/**
 * Unit tests for ShellStateService scroll navigation methods
 *
 * Validates: Requirements 1.1, 1.4, 2.1, 2.4, 3.4, 4.6, 4.8, 5.3, 7.2, 7.3, 7.5, 8.1, 8.3, 8.4
 */

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

// Mock MessageBusClient module to avoid bridge dependency
let mockSend: jest.Mock = jest.fn();
let mockCancel: jest.Mock = jest.fn();
let mockConfigure: jest.Mock = jest.fn();
let mockSubscribeHandlers: Map<string, (msg: any) => void> = new Map();
let mockUnsubscribe: jest.Mock = jest.fn();

jest.mock('../services/message-bus-client.service', () => ({
  MessageBusClient: class MockMessageBusClient {
    send = (...args: any[]) => mockSend(...args);
    cancel = (...args: any[]) => mockCancel(...args);
    configure = (...args: any[]) => mockConfigure(...args);
    subscribe = (messageType: string, handler: (msg: any) => void) => {
      mockSubscribeHandlers.set(messageType, handler);
      return { unsubscribe: mockUnsubscribe };
    };
  },
}));

// Mock @angular/core to provide signal, computed, inject
let injectMap: Map<any, any> = new Map();

jest.mock('@angular/core', () => {
  function signal<T>(initialValue: T) {
    let value = initialValue;
    const fn = () => value;
    fn.set = (v: T) => { value = v; };
    fn.update = (updater: (v: T) => T) => { value = updater(value); };
    return fn;
  }

  function computed<T>(fn: () => T) {
    return fn;
  }

  function inject(token: any) {
    return injectMap.get(token);
  }

  return {
    Injectable: () => (target: any) => target,
    OnDestroy: class {},
    signal,
    computed,
    inject,
  };
});

import { ShellStateService } from './shell-state.service';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage } from '../services/message-bus.types';

describe('ShellStateService scroll navigation', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    jest.useFakeTimers();
    uuidCounter = 0;
    correlationCounter = 0;
    mockSubscribeHandlers = new Map();
    mockUnsubscribe = jest.fn();
    mockCancel = jest.fn();
    mockConfigure = jest.fn();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.useRealTimers();
    jest.restoreAllMocks();
  });

  // --- Helpers ---

  function simulateOpenFileResponse(payload: string): void {
    const corrId = `corr-${correlationCounter}`;
    const handler = mockSubscribeHandlers.get('open-file');
    handler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload,
    } as InboundMessage);
  }

  function simulateScrollInfoResponse(payload: string): void {
    const handler = mockSubscribeHandlers.get('get-scroll-info');
    handler!({
      messageType: 'get-scroll-info',
      correlationId: '',
      payload,
    } as InboundMessage);
  }

  function simulateViewResponse(correlationId: string, payload: string): void {
    const handler = mockSubscribeHandlers.get('get-view');
    handler!({
      messageType: 'get-view',
      correlationId,
      payload,
    } as InboundMessage);
  }

  function simulateScanComplete(viewSessionId: string): void {
    const handler = mockSubscribeHandlers.get('scan-complete');
    handler!({
      messageType: 'scan-complete',
      correlationId: '',
      payload: viewSessionId,
    } as InboundMessage);
  }

  /**
   * Opens a tab and sets up scrollbar state with scrollable content.
   * Returns the viewSessionId.
   */
  function openTabWithScrollableContent(): string {
    service.triggerOpenFile();
    const viewSessionId = `vs-${uuidCounter + 1}`;
    simulateOpenFileResponse(`${viewSessionId}\n/test/file.txt\nline1\nline2\nline3`);

    // Set view dimensions
    service.viewDimensions.set({ rowCount: 40, colCount: 120 });

    // Set scrollbar state to scrollable (verticalMax > rowCount, horizontalMax > colCount)
    simulateScrollInfoResponse('ScanComplete\n1000\n256\n200');

    return viewSessionId;
  }

  // --- Drag start captures initial state correctly (Req 1.1, 2.1) ---

  describe('drag start captures initial state correctly', () => {
    it('vertical drag start captures startLine and sets DragState', () => {
      const vsId = openTabWithScrollableContent();

      service.handleVerticalDragStart(100, 500);

      const drag = service.dragState();
      expect(drag).not.toBeNull();
      expect(drag!.axis).toBe('vertical');
      expect(drag!.startMousePos).toBe(100);
      expect(drag!.startScrollPos).toBe(0); // startLine is 0 initially
      expect(drag!.trackLength).toBe(500);
      expect(drag!.scrollbarMax).toBe(1000);
      expect(drag!.viewportSize).toBe(40);
    });

    it('horizontal drag start captures startCol and sets DragState', () => {
      const vsId = openTabWithScrollableContent();

      service.handleHorizontalDragStart(200, 600);

      const drag = service.dragState();
      expect(drag).not.toBeNull();
      expect(drag!.axis).toBe('horizontal');
      expect(drag!.startMousePos).toBe(200);
      expect(drag!.startScrollPos).toBe(0); // startCol is 0 initially
      expect(drag!.trackLength).toBe(600);
      expect(drag!.scrollbarMax).toBe(200);
      expect(drag!.viewportSize).toBe(120);
    });

    it('vertical drag start captures current startLine when scrolled', () => {
      const vsId = openTabWithScrollableContent();

      // Scroll down first via wheel
      service.handleWheel(1, 0);
      const state = service.tabViewStates().get(vsId);
      expect(state!.startLine).toBe(3); // WHEEL_STEP = 3

      // Now start drag — should capture current startLine
      service.handleVerticalDragStart(100, 500);

      const drag = service.dragState();
      expect(drag!.startScrollPos).toBe(3);
    });
  });

  // --- Drag end clears state and sends view request (Req 1.4, 2.4) ---

  describe('drag end clears state and sends view request', () => {
    it('clears dragState to null on drag end', () => {
      openTabWithScrollableContent();
      service.handleVerticalDragStart(100, 500);
      expect(service.dragState()).not.toBeNull();

      service.handleDragEnd();
      expect(service.dragState()).toBeNull();
    });

    it('sends get-view request on drag end', () => {
      const vsId = openTabWithScrollableContent();
      service.handleVerticalDragStart(100, 500);

      // Move the thumb to change position
      service.handleDragMove(200); // delta = 100px

      mockSend.mockClear();
      correlationCounter = 100; // reset for clarity
      service.handleDragEnd();

      // Should have sent a get-view request
      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(1);

      // Payload should contain the viewSessionId and current scroll position
      const payload = getViewCalls[0][1] as string;
      expect(payload).toContain(vsId);
    });

    it('horizontal drag end sends view request with final startCol', () => {
      const vsId = openTabWithScrollableContent();
      service.handleHorizontalDragStart(200, 600);

      // Move the thumb
      service.handleDragMove(250); // delta = 50px

      mockSend.mockClear();
      correlationCounter = 200;
      service.handleDragEnd();

      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(1);
    });
  });

  // --- No view request when position unchanged at boundary (Req 3.4, 4.6) ---

  describe('no view request when position unchanged at boundary', () => {
    it('wheel up at startLine=0 does not send view request', () => {
      openTabWithScrollableContent();

      // startLine is already 0, wheel up should not change it
      mockSend.mockClear();
      service.handleWheel(-1, 0);

      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(0);
    });

    it('arrow up at startLine=0 does not send view request', () => {
      openTabWithScrollableContent();

      mockSend.mockClear();
      service.handleArrowKey('up');

      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(0);
    });

    it('arrow left at startCol=0 does not send view request', () => {
      openTabWithScrollableContent();

      mockSend.mockClear();
      service.handleArrowKey('left');

      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(0);
    });

    it('wheel down at max boundary does not send view request', () => {
      const vsId = openTabWithScrollableContent();

      // Set startLine to max (verticalMax - rowCount = 1000 - 40 = 960)
      const states = service.tabViewStates();
      const existing = states.get(vsId)!;
      const updated = new Map(states);
      updated.set(vsId, { ...existing, startLine: 960 });
      service.tabViewStates.set(updated);

      mockSend.mockClear();
      service.handleWheel(1, 0);

      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(0);
    });
  });

  // --- Arrow keys ignored when no active tab (Req 4.8) ---

  describe('arrow keys ignored when no active tab', () => {
    it('handleArrowKey does nothing when no tabs are open', () => {
      // No tabs open, activeTab() returns null
      service.viewDimensions.set({ rowCount: 40, colCount: 120 });

      mockSend.mockClear();
      service.handleArrowKey('down');
      service.handleArrowKey('up');
      service.handleArrowKey('left');
      service.handleArrowKey('right');

      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(0);
    });
  });

  // --- Latest-wins cancellation (Req 7.2) ---

  describe('latest-wins cancellation — pending request cancelled on new scroll', () => {
    it('cancels pending request when a new scroll action occurs', () => {
      const vsId = openTabWithScrollableContent();

      // First wheel scroll — sends a view request
      service.handleWheel(1, 0);
      const state1 = service.tabViewStates().get(vsId);
      const firstCorrelationId = state1!.pendingCorrelationId;
      expect(firstCorrelationId).not.toBeNull();

      // Second wheel scroll — should cancel the first and send a new one
      service.handleWheel(1, 0);

      expect(mockCancel).toHaveBeenCalledWith(firstCorrelationId);

      const state2 = service.tabViewStates().get(vsId);
      expect(state2!.pendingCorrelationId).not.toBe(firstCorrelationId);
      expect(state2!.pendingCorrelationId).not.toBeNull();
    });

    it('cancels pending request when drag end sends new request', () => {
      const vsId = openTabWithScrollableContent();

      // Wheel scroll creates a pending request
      service.handleWheel(1, 0);
      const state1 = service.tabViewStates().get(vsId);
      const firstCorrelationId = state1!.pendingCorrelationId;

      // Start and end a drag — drag end should cancel the pending wheel request
      service.handleVerticalDragStart(100, 500);
      service.handleDragMove(150);
      service.handleDragEnd();

      expect(mockCancel).toHaveBeenCalledWith(firstCorrelationId);
    });
  });

  // --- View response updates displayed rows (Req 7.3, 8.1) ---

  describe('view response updates displayed rows', () => {
    it('successful view response replaces viewRows', () => {
      const vsId = openTabWithScrollableContent();

      // Trigger a scroll to create a pending request
      service.handleWheel(1, 0);
      const state = service.tabViewStates().get(vsId);
      const corrId = state!.pendingCorrelationId!;

      // Simulate successful response (non-wrapped format: lineNum\tcontent)
      simulateViewResponse(corrId, '4\tnew-line-1\n5\tnew-line-2\n6\tnew-line-3');

      const updatedState = service.tabViewStates().get(vsId);
      expect(updatedState!.viewRows).toEqual(['new-line-1', 'new-line-2', 'new-line-3']);
    });

    it('successful view response clears pendingCorrelationId', () => {
      const vsId = openTabWithScrollableContent();

      service.handleWheel(1, 0);
      const state = service.tabViewStates().get(vsId);
      const corrId = state!.pendingCorrelationId!;

      simulateViewResponse(corrId, '4\trow1\n5\trow2');

      const updatedState = service.tabViewStates().get(vsId);
      expect(updatedState!.pendingCorrelationId).toBeNull();
    });
  });

  // --- Previous rows preserved while pending (Req 8.3) ---

  describe('previous rows preserved while pending', () => {
    it('viewRows remain unchanged while a scroll request is pending', () => {
      const vsId = openTabWithScrollableContent();

      // The tab already has initial rows from open-file response
      const initialRows = service.tabViewStates().get(vsId)!.viewRows;
      expect(initialRows).toEqual(['line1', 'line2', 'line3']);

      // Trigger a scroll — creates a pending request but rows should stay
      service.handleWheel(1, 0);

      const pendingState = service.tabViewStates().get(vsId);
      expect(pendingState!.pendingCorrelationId).not.toBeNull();
      expect(pendingState!.viewRows).toEqual(['line1', 'line2', 'line3']);
    });
  });

  // --- Error response keeps rows, shows error (Req 8.4) ---

  describe('error response keeps rows, shows error', () => {
    it('error view response preserves previous viewRows', () => {
      const vsId = openTabWithScrollableContent();

      // Trigger scroll
      service.handleWheel(1, 0);
      const state = service.tabViewStates().get(vsId);
      const corrId = state!.pendingCorrelationId!;

      // Simulate error response
      simulateViewResponse(corrId, 'ERROR: Session expired');

      const updatedState = service.tabViewStates().get(vsId);
      expect(updatedState!.viewRows).toEqual(['line1', 'line2', 'line3']);
    });

    it('error view response stores error message', () => {
      const vsId = openTabWithScrollableContent();

      service.handleWheel(1, 0);
      const state = service.tabViewStates().get(vsId);
      const corrId = state!.pendingCorrelationId!;

      simulateViewResponse(corrId, 'ERROR: Session expired');

      const updatedState = service.tabViewStates().get(vsId);
      expect(updatedState!.errorMessage).toBe('ERROR: Session expired');
    });

    it('error view response clears pendingCorrelationId', () => {
      const vsId = openTabWithScrollableContent();

      service.handleWheel(1, 0);
      const state = service.tabViewStates().get(vsId);
      const corrId = state!.pendingCorrelationId!;

      simulateViewResponse(corrId, 'ERROR: Something went wrong');

      const updatedState = service.tabViewStates().get(vsId);
      expect(updatedState!.pendingCorrelationId).toBeNull();
    });
  });

  // --- Tab switch restores thumb position without new request (Req 7.5) ---

  describe('tab switch restores thumb position without new request', () => {
    it('switching to a tab with cached rows does not send a new view request', () => {
      // Open first tab with scrollable content
      const vsId1 = openTabWithScrollableContent();

      // Scroll the first tab
      service.handleWheel(1, 0);
      const state1 = service.tabViewStates().get(vsId1);
      const corrId1 = state1!.pendingCorrelationId!;
      simulateViewResponse(corrId1, '4\tscrolled-row1\n5\tscrolled-row2');

      // Open second tab
      service.triggerOpenFile();
      const vsId2 = `vs-${uuidCounter + 1}`;
      const corrId = `corr-${correlationCounter}`;
      const handler = mockSubscribeHandlers.get('open-file');
      handler!({
        messageType: 'open-file',
        correlationId: corrId,
        payload: `${vsId2}\n/test/file2.txt\nother-line1`,
      } as InboundMessage);

      // Switch back to first tab
      const tabs = service.tabs();
      mockSend.mockClear();
      service.activateTab(tabs[0].id);

      // No get-view request should be sent (cached rows exist)
      const getViewCalls = mockSend.mock.calls.filter(
        (c: any[]) => c[0] === 'get-view'
      );
      expect(getViewCalls.length).toBe(0);
    });

    it('thumb fraction reflects stored startLine after tab switch', () => {
      const vsId1 = openTabWithScrollableContent();

      // Scroll the first tab to startLine=3
      service.handleWheel(1, 0);
      const state1 = service.tabViewStates().get(vsId1);
      expect(state1!.startLine).toBe(3);
      const corrId1 = state1!.pendingCorrelationId!;
      simulateViewResponse(corrId1, '4\tscrolled-row1\n5\tscrolled-row2');

      // Open second tab
      service.triggerOpenFile();
      const vsId2 = `vs-${uuidCounter + 1}`;
      const corrId = `corr-${correlationCounter}`;
      const handler = mockSubscribeHandlers.get('open-file');
      handler!({
        messageType: 'open-file',
        correlationId: corrId,
        payload: `${vsId2}\n/test/file2.txt\nother-line1`,
      } as InboundMessage);

      // Switch back to first tab
      const tabs = service.tabs();
      service.activateTab(tabs[0].id);

      // verticalThumbFraction should reflect startLine=3
      // fraction = startLine / (verticalMax - rowCount) = 3 / (1000 - 40) = 3/960
      const fraction = service.verticalThumbFraction();
      expect(fraction).toBeCloseTo(3 / 960);
    });
  });

  // --- Thumb position recomputed on view response (Req 5.3) ---

  describe('thumb position recomputed on view response', () => {
    it('verticalThumbFraction reflects startLine after scroll and response', () => {
      const vsId = openTabWithScrollableContent();

      // Initially at 0
      expect(service.verticalThumbFraction()).toBe(0);

      // Scroll down
      service.handleWheel(1, 0);

      // startLine should now be 3 (WHEEL_STEP)
      const state = service.tabViewStates().get(vsId);
      expect(state!.startLine).toBe(3);

      // Thumb fraction should already reflect the new position
      // (computed signals update immediately from startLine change)
      // fraction = 3 / (1000 - 40) = 3/960
      expect(service.verticalThumbFraction()).toBeCloseTo(3 / 960);
    });

    it('horizontalThumbFraction reflects startCol after scroll', () => {
      const vsId = openTabWithScrollableContent();

      expect(service.horizontalThumbFraction()).toBe(0);

      // Scroll right
      service.handleWheel(0, 1);

      const state = service.tabViewStates().get(vsId);
      expect(state!.startCol).toBe(3);

      // fraction = 3 / (200 - 120) = 3/80
      expect(service.horizontalThumbFraction()).toBeCloseTo(3 / 80);
    });

    it('thumb fraction updates after view response confirms new position', () => {
      const vsId = openTabWithScrollableContent();

      service.handleWheel(1, 0);
      const state = service.tabViewStates().get(vsId);
      const corrId = state!.pendingCorrelationId!;

      // Before response, fraction already reflects startLine=3
      expect(service.verticalThumbFraction()).toBeCloseTo(3 / 960);

      // After response, fraction remains the same (startLine unchanged by response)
      simulateViewResponse(corrId, '4\tnew-row1\n5\tnew-row2');
      expect(service.verticalThumbFraction()).toBeCloseTo(3 / 960);
    });
  });
});
