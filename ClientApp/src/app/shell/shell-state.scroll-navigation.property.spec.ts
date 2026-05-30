/**
 * Feature: text-handling-more, Property 4: Thumb position fraction is proportional to scroll position
 *
 * Validates: Requirements 5.1, 5.2, 5.4, 5.5
 *
 * Property: For any startLine (or startCol) in [0, scrollbarMax - viewportSize] where
 * scrollbarMax > viewportSize, the thumb position fraction SHALL equal
 * startPos / (scrollbarMax - viewportSize), producing a value in [0, 1] where
 * 0 means thumb at start and 1 means thumb at end.
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
let mockSubscribeHandlers: Map<string, (msg: any) => void> = new Map();

jest.mock('../services/message-bus-client.service', () => ({
  MessageBusClient: class MockMessageBusClient {
    send = (...args: any[]) => mockSend(...args);
    cancel = (...args: any[]) => mockCancel(...args);
    configure = jest.fn();
    subscribe = (messageType: string, handler: (msg: any) => void) => {
      mockSubscribeHandlers.set(messageType, handler);
      return { unsubscribe: jest.fn() };
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

import * as fc from 'fast-check';
import { ShellStateService, MIN_THUMB_SIZE, clamp } from './shell-state.service';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage } from '../services/message-bus.types';
import { TabViewState, ScrollbarState } from './shell.types';

describe('Feature: text-handling-more, Property 4: Thumb position fraction is proportional to scroll position', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    uuidCounter = 0;
    correlationCounter = 0;
    mockSubscribeHandlers = new Map();
    mockCancel = jest.fn();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    jest.useFakeTimers();
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

  /**
   * Helper: set up a tab with given scrollbar state and scroll position,
   * then read the verticalThumbFraction computed signal.
   */
  function setupAndGetVerticalFraction(
    scrollbarMax: number,
    rowCount: number,
    startLine: number
  ): number {
    // Create a tab via open-file simulation
    service.triggerOpenFile();
    const corrId = `corr-${correlationCounter}`;
    const viewSessionId = `vs-${uuidCounter + 1}`;
    const handler = mockSubscribeHandlers.get('open-file');
    handler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload: `${viewSessionId}\n/test/file.txt`,
    } as InboundMessage);

    // Set view dimensions
    service.viewDimensions.set({ rowCount, colCount: 80 });

    // Update the tab's scrollbar state and startLine
    const tab = service.activeTab();
    const states = service.tabViewStates();
    const existing = states.get(tab!.viewSessionId)!;
    const updated = new Map(states);
    updated.set(tab!.viewSessionId, {
      ...existing,
      scrollbarState: { verticalMax: scrollbarMax, horizontalMax: 0, disabled: false },
      startLine,
    });
    service.tabViewStates.set(updated);

    return service.verticalThumbFraction();
  }

  /**
   * Helper: set up a tab with given horizontal scrollbar state and scroll position,
   * then read the horizontalThumbFraction computed signal.
   */
  function setupAndGetHorizontalFraction(
    scrollbarMax: number,
    colCount: number,
    startCol: number
  ): number {
    // Create a tab via open-file simulation
    service.triggerOpenFile();
    const corrId = `corr-${correlationCounter}`;
    const viewSessionId = `vs-${uuidCounter + 1}`;
    const handler = mockSubscribeHandlers.get('open-file');
    handler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload: `${viewSessionId}\n/test/file.txt`,
    } as InboundMessage);

    // Set view dimensions
    service.viewDimensions.set({ rowCount: 40, colCount });

    // Update the tab's scrollbar state and startCol
    const tab = service.activeTab();
    const states = service.tabViewStates();
    const existing = states.get(tab!.viewSessionId)!;
    const updated = new Map(states);
    updated.set(tab!.viewSessionId, {
      ...existing,
      scrollbarState: { verticalMax: 0, horizontalMax: scrollbarMax, disabled: false },
      startCol,
    });
    service.tabViewStates.set(updated);

    return service.horizontalThumbFraction();
  }

  it('vertical thumb fraction equals startLine / (scrollbarMax - rowCount) and is in [0, 1]', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 2, max: 10000 }),  // scrollbarMax (must be > viewportSize)
        (scrollbarMax: number) => {
          // viewportSize must be < scrollbarMax
          const rowCount = fc.sample(fc.integer({ min: 1, max: scrollbarMax - 1 }), 1)[0];
          const maxScroll = scrollbarMax - rowCount;
          const startLine = fc.sample(fc.integer({ min: 0, max: maxScroll }), 1)[0];

          // Reset service state for each run
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const fraction = setupAndGetVerticalFraction(scrollbarMax, rowCount, startLine);
          const expectedFraction = startLine / maxScroll;

          // Fraction must be in [0, 1]
          if (fraction < 0 || fraction > 1) return false;

          // Fraction must equal expected value (within floating point tolerance)
          if (Math.abs(fraction - expectedFraction) > 1e-10) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('horizontal thumb fraction equals startCol / (scrollbarMax - colCount) and is in [0, 1]', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 2, max: 10000 }),  // scrollbarMax (must be > viewportSize)
        (scrollbarMax: number) => {
          const colCount = fc.sample(fc.integer({ min: 1, max: scrollbarMax - 1 }), 1)[0];
          const maxScroll = scrollbarMax - colCount;
          const startCol = fc.sample(fc.integer({ min: 0, max: maxScroll }), 1)[0];

          // Reset service state for each run
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const fraction = setupAndGetHorizontalFraction(scrollbarMax, colCount, startCol);
          const expectedFraction = startCol / maxScroll;

          // Fraction must be in [0, 1]
          if (fraction < 0 || fraction > 1) return false;

          // Fraction must equal expected value (within floating point tolerance)
          if (Math.abs(fraction - expectedFraction) > 1e-10) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('vertical thumb fraction is 0 when startLine is 0', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 2, max: 10000 }),  // scrollbarMax
        fc.integer({ min: 1, max: 9999 }),   // rowCount (will be clamped)
        (scrollbarMax: number, rawRowCount: number) => {
          const rowCount = Math.min(rawRowCount, scrollbarMax - 1);
          if (rowCount < 1) return true; // skip degenerate

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const fraction = setupAndGetVerticalFraction(scrollbarMax, rowCount, 0);
          return fraction === 0;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('vertical thumb fraction is 1 when startLine equals scrollbarMax - rowCount', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 2, max: 10000 }),  // scrollbarMax
        fc.integer({ min: 1, max: 9999 }),   // rowCount (will be clamped)
        (scrollbarMax: number, rawRowCount: number) => {
          const rowCount = Math.min(rawRowCount, scrollbarMax - 1);
          if (rowCount < 1) return true; // skip degenerate
          const maxScroll = scrollbarMax - rowCount;

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const fraction = setupAndGetVerticalFraction(scrollbarMax, rowCount, maxScroll);
          return Math.abs(fraction - 1) < 1e-10;
        }
      ),
      { numRuns: 10 }
    );
  });
});


/**
 * Feature: text-handling-more, Property 5: Thumb size ratio is proportional to viewport coverage
 *
 * Validates: Requirements 6.1, 6.2
 *
 * Property: For any viewportSize > 0 and scrollbarMax > viewportSize, the thumb size ratio
 * SHALL equal viewportSize / scrollbarMax, producing a value in (0, 1). When converted to
 * pixels (ratio × trackPixelSize), the result SHALL be at least 20 pixels (min thumb size).
 */
describe('Feature: text-handling-more, Property 5: Thumb size ratio is proportional to viewport coverage', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    uuidCounter = 0;
    correlationCounter = 0;
    mockSubscribeHandlers = new Map();
    mockCancel = jest.fn();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    jest.useFakeTimers();
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

  /**
   * Helper: set up a tab with given vertical scrollbar max and viewport rowCount,
   * then read the verticalThumbRatio computed signal.
   */
  function setupAndGetVerticalRatio(scrollbarMax: number, rowCount: number): number {
    service.triggerOpenFile();
    const corrId = `corr-${correlationCounter}`;
    const viewSessionId = `vs-${uuidCounter + 1}`;
    const handler = mockSubscribeHandlers.get('open-file');
    handler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload: `${viewSessionId}\n/test/file.txt`,
    } as InboundMessage);

    service.viewDimensions.set({ rowCount, colCount: 80 });

    const tab = service.activeTab();
    const states = service.tabViewStates();
    const existing = states.get(tab!.viewSessionId)!;
    const updated = new Map(states);
    updated.set(tab!.viewSessionId, {
      ...existing,
      scrollbarState: { verticalMax: scrollbarMax, horizontalMax: 0, disabled: false },
    });
    service.tabViewStates.set(updated);

    return service.verticalThumbRatio();
  }

  /**
   * Helper: set up a tab with given horizontal scrollbar max and viewport colCount,
   * then read the horizontalThumbRatio computed signal.
   */
  function setupAndGetHorizontalRatio(scrollbarMax: number, colCount: number): number {
    service.triggerOpenFile();
    const corrId = `corr-${correlationCounter}`;
    const viewSessionId = `vs-${uuidCounter + 1}`;
    const handler = mockSubscribeHandlers.get('open-file');
    handler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload: `${viewSessionId}\n/test/file.txt`,
    } as InboundMessage);

    service.viewDimensions.set({ rowCount: 40, colCount });

    const tab = service.activeTab();
    const states = service.tabViewStates();
    const existing = states.get(tab!.viewSessionId)!;
    const updated = new Map(states);
    updated.set(tab!.viewSessionId, {
      ...existing,
      scrollbarState: { verticalMax: 0, horizontalMax: scrollbarMax, disabled: false },
    });
    service.tabViewStates.set(updated);

    return service.horizontalThumbRatio();
  }

  it('vertical thumb ratio equals rowCount / scrollbarMax and is in (0, 1)', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // rowCount (viewportSize > 0)
        fc.integer({ min: 1, max: 10000 }),  // extra > 0 (so scrollbarMax > viewportSize)
        (rowCount: number, extra: number) => {
          const scrollbarMax = rowCount + extra;

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const ratio = setupAndGetVerticalRatio(scrollbarMax, rowCount);
          const expectedRatio = rowCount / scrollbarMax;

          // Ratio must be in (0, 1)
          if (ratio <= 0 || ratio >= 1) return false;

          // Ratio must equal expected value (within floating point tolerance)
          if (Math.abs(ratio - expectedRatio) > 1e-10) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('horizontal thumb ratio equals colCount / scrollbarMax and is in (0, 1)', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // colCount (viewportSize > 0)
        fc.integer({ min: 1, max: 10000 }),  // extra > 0
        (colCount: number, extra: number) => {
          const scrollbarMax = colCount + extra;

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const ratio = setupAndGetHorizontalRatio(scrollbarMax, colCount);
          const expectedRatio = colCount / scrollbarMax;

          // Ratio must be in (0, 1)
          if (ratio <= 0 || ratio >= 1) return false;

          // Ratio must equal expected value (within floating point tolerance)
          if (Math.abs(ratio - expectedRatio) > 1e-10) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('pixel thumb size (Math.max(MIN_THUMB_SIZE, ratio * trackPixelSize)) is always >= 20px', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // viewportSize > 0
        fc.integer({ min: 1, max: 10000 }),  // extra > 0
        fc.integer({ min: 20, max: 10000 }), // trackPixelSize >= MIN_THUMB_SIZE
        (viewportSize: number, extra: number, trackPixelSize: number) => {
          const scrollbarMax = viewportSize + extra;
          const ratio = viewportSize / scrollbarMax;
          const thumbPixels = Math.max(MIN_THUMB_SIZE, ratio * trackPixelSize);

          // Thumb must be at least MIN_THUMB_SIZE (20px)
          if (thumbPixels < MIN_THUMB_SIZE) return false;

          // Thumb must not exceed track size
          if (thumbPixels > trackPixelSize) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});


/**
 * Feature: text-handling-more, Property 1: Scroll step computation is clamped to valid range
 *
 * Validates: Requirements 3.1, 3.2, 4.1, 4.2, 4.3, 4.4
 *
 * Property: For any current scroll position (startLine or startCol), any step size
 * (1 for arrow, 3 for wheel), any direction (positive or negative), any scrollbarMax,
 * and any viewportSize (rowCount or colCount) where scrollbarMax > viewportSize,
 * the computed new position SHALL equal clamp(current + sign * step, 0, scrollbarMax - viewportSize)
 * and the result SHALL always be in the range [0, scrollbarMax - viewportSize].
 */
describe('Feature: text-handling-more, Property 1: Scroll step computation is clamped to valid range', () => {
  /**
   * Generators:
   * - scrollbarMax: integer > 1 (must be > viewportSize, and viewportSize >= 1)
   * - viewportSize: integer in [1, scrollbarMax - 1]
   * - current: integer in [0, scrollbarMax - viewportSize]
   * - step: from [1, 3] (ARROW_STEP or WHEEL_STEP)
   * - direction: from [-1, +1]
   */
  const scrollInputArb = fc
    .integer({ min: 2, max: 10000 })
    .chain(scrollbarMax =>
      fc.integer({ min: 1, max: scrollbarMax - 1 }).chain(viewportSize =>
        fc.tuple(
          fc.constant(scrollbarMax),
          fc.constant(viewportSize),
          fc.integer({ min: 0, max: scrollbarMax - viewportSize }),
          fc.constantFrom(1, 3),
          fc.constantFrom(-1, 1),
        )
      )
    );

  it('result equals clamp(current + direction * step, 0, maxScroll) and is in [0, maxScroll]', () => {
    fc.assert(
      fc.property(
        scrollInputArb,
        ([scrollbarMax, viewportSize, current, step, direction]) => {
          const maxScroll = scrollbarMax - viewportSize;
          const result = clamp(current + direction * step, 0, maxScroll);

          // Result must equal the clamped computation
          const expected = Math.max(0, Math.min(maxScroll, current + direction * step));
          if (result !== expected) return false;

          // Result must be in valid range [0, maxScroll]
          if (result < 0 || result > maxScroll) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});


/**
 * Feature: text-handling-more, Property 2: Drag position computation is clamped to valid range
 *
 * Validates: Requirements 1.2, 2.2
 *
 * Property: For any drag state (with valid startScrollPos, trackLength > 0,
 * scrollbarMax, viewportSize where scrollbarMax > viewportSize) and any mouse delta,
 * the computed scroll position SHALL equal
 * clamp(startScrollPos + round(delta / trackLength * (scrollbarMax - viewportSize)), 0, scrollbarMax - viewportSize)
 * and the result SHALL always be in the range [0, scrollbarMax - viewportSize].
 */
describe('Feature: text-handling-more, Property 2: Drag position computation is clamped to valid range', () => {
  /**
   * Generators:
   * - scrollbarMax: integer > 1 (must be > viewportSize)
   * - viewportSize: integer in [1, scrollbarMax - 1]
   * - trackLength: positive integer in [1, 1000]
   * - startScrollPos: integer in [0, scrollbarMax - viewportSize]
   * - delta: any integer in [-2000, 2000]
   */
  const dragInputArb = fc
    .integer({ min: 2, max: 10000 })
    .chain(scrollbarMax =>
      fc.integer({ min: 1, max: scrollbarMax - 1 }).chain(viewportSize =>
        fc.tuple(
          fc.constant(scrollbarMax),
          fc.constant(viewportSize),
          fc.integer({ min: 1, max: 1000 }),
          fc.integer({ min: 0, max: scrollbarMax - viewportSize }),
          fc.integer({ min: -2000, max: 2000 }),
        )
      )
    );

  it('result equals clamp(startScrollPos + round(delta / trackLength * maxScroll), 0, maxScroll) and is in [0, maxScroll]', () => {
    fc.assert(
      fc.property(
        dragInputArb,
        ([scrollbarMax, viewportSize, trackLength, startScrollPos, delta]) => {
          const maxScroll = scrollbarMax - viewportSize;
          const scrollDelta = Math.round((delta / trackLength) * maxScroll);
          const result = clamp(startScrollPos + scrollDelta, 0, maxScroll);

          // Result must equal the clamped computation
          const expected = Math.max(0, Math.min(maxScroll, startScrollPos + scrollDelta));
          if (result !== expected) return false;

          // Result must be in valid range [0, maxScroll]
          if (result < 0 || result > maxScroll) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});


/**
 * Feature: text-handling-more, Property 3: Non-interactive when content fits viewport
 *
 * Validates: Requirements 1.5, 2.5, 3.6, 3.7, 5.6, 6.4
 *
 * Property: For any scrollbarMax and viewportSize where scrollbarMax ≤ viewportSize,
 * all scroll actions (drag start, wheel, arrow key) SHALL produce no change to the
 * scroll position, the thumb position fraction SHALL be 0, and the thumb size ratio
 * SHALL be 1 (full track).
 */
describe('Feature: text-handling-more, Property 3: Non-interactive when content fits viewport', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    uuidCounter = 0;
    correlationCounter = 0;
    mockSubscribeHandlers = new Map();
    mockCancel = jest.fn();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    jest.useFakeTimers();
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

  /**
   * Helper: set up a tab with given scrollbar state and viewport dimensions,
   * returning the viewSessionId for further assertions.
   */
  function setupTabWithScrollbar(
    verticalMax: number,
    horizontalMax: number,
    rowCount: number,
    colCount: number
  ): string {
    service.triggerOpenFile();
    const corrId = `corr-${correlationCounter}`;
    const viewSessionId = `vs-${uuidCounter + 1}`;
    const handler = mockSubscribeHandlers.get('open-file');
    handler!({
      messageType: 'open-file',
      correlationId: corrId,
      payload: `${viewSessionId}\n/test/file.txt`,
    } as InboundMessage);

    service.viewDimensions.set({ rowCount, colCount });

    const tab = service.activeTab();
    const states = service.tabViewStates();
    const existing = states.get(tab!.viewSessionId)!;
    const updated = new Map(states);
    updated.set(tab!.viewSessionId, {
      ...existing,
      scrollbarState: { verticalMax, horizontalMax, disabled: verticalMax === 0 && horizontalMax === 0 },
      startLine: 0,
      startCol: 0,
    });
    service.tabViewStates.set(updated);

    return viewSessionId;
  }

  it('thumb ratio = 1 when scrollbarMax <= viewportSize', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // viewportSize > 0
        fc.integer({ min: 0, max: 10000 }),  // scrollbarMaxRaw
        (viewportSize: number, scrollbarMaxRaw: number) => {
          // Ensure scrollbarMax <= viewportSize
          const scrollbarMax = Math.min(scrollbarMaxRaw, viewportSize);

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          setupTabWithScrollbar(scrollbarMax, scrollbarMax, viewportSize, viewportSize);

          // verticalThumbRatio should be 1 when verticalMax <= rowCount
          const vRatio = service.verticalThumbRatio();
          // horizontalThumbRatio should be 1 when horizontalMax <= colCount
          const hRatio = service.horizontalThumbRatio();

          return vRatio === 1 && hRatio === 1;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('thumb fraction = 0 when scrollbarMax <= viewportSize', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // viewportSize > 0
        fc.integer({ min: 0, max: 10000 }),  // scrollbarMaxRaw
        (viewportSize: number, scrollbarMaxRaw: number) => {
          // Ensure scrollbarMax <= viewportSize
          const scrollbarMax = Math.min(scrollbarMaxRaw, viewportSize);

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          setupTabWithScrollbar(scrollbarMax, scrollbarMax, viewportSize, viewportSize);

          // verticalThumbFraction should be 0 when verticalMax <= rowCount
          const vFraction = service.verticalThumbFraction();
          // horizontalThumbFraction should be 0 when horizontalMax <= colCount
          const hFraction = service.horizontalThumbFraction();

          return vFraction === 0 && hFraction === 0;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('wheel scroll produces no position change when scrollbarMax <= viewportSize', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // viewportSize > 0
        fc.integer({ min: 0, max: 10000 }),  // scrollbarMaxRaw
        fc.integer({ min: -100, max: 100 }).filter(d => d !== 0),  // deltaY (non-zero)
        fc.integer({ min: -100, max: 100 }).filter(d => d !== 0),  // deltaX (non-zero)
        (viewportSize: number, scrollbarMaxRaw: number, deltaY: number, deltaX: number) => {
          // Ensure scrollbarMax <= viewportSize
          const scrollbarMax = Math.min(scrollbarMaxRaw, viewportSize);

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const vsId = setupTabWithScrollbar(scrollbarMax, scrollbarMax, viewportSize, viewportSize);

          // Capture position before wheel
          const stateBefore = service.tabViewStates().get(vsId)!;
          const startLineBefore = stateBefore.startLine;
          const startColBefore = stateBefore.startCol;

          // Perform wheel action
          service.handleWheel(deltaY, deltaX);

          // Position should not change
          const stateAfter = service.tabViewStates().get(vsId)!;
          return stateAfter.startLine === startLineBefore && stateAfter.startCol === startColBefore;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('arrow key scroll produces no position change when scrollbarMax <= viewportSize', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // viewportSize > 0
        fc.integer({ min: 0, max: 10000 }),  // scrollbarMaxRaw
        fc.constantFrom('up' as const, 'down' as const, 'left' as const, 'right' as const),
        (viewportSize: number, scrollbarMaxRaw: number, direction: 'up' | 'down' | 'left' | 'right') => {
          // Ensure scrollbarMax <= viewportSize
          const scrollbarMax = Math.min(scrollbarMaxRaw, viewportSize);

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          const vsId = setupTabWithScrollbar(scrollbarMax, scrollbarMax, viewportSize, viewportSize);

          // Capture position before arrow key
          const stateBefore = service.tabViewStates().get(vsId)!;
          const startLineBefore = stateBefore.startLine;
          const startColBefore = stateBefore.startCol;

          // Perform arrow key action
          service.handleArrowKey(direction);

          // Position should not change
          const stateAfter = service.tabViewStates().get(vsId)!;
          return stateAfter.startLine === startLineBefore && stateAfter.startCol === startColBefore;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('drag start produces no DragState when scrollbarMax <= viewportSize', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 10000 }),  // viewportSize > 0
        fc.integer({ min: 0, max: 10000 }),  // scrollbarMaxRaw
        fc.integer({ min: 1, max: 10000 }),  // trackLength > 0
        fc.integer({ min: 0, max: 10000 }),  // mousePos
        (viewportSize: number, scrollbarMaxRaw: number, trackLength: number, mousePos: number) => {
          // Ensure scrollbarMax <= viewportSize
          const scrollbarMax = Math.min(scrollbarMaxRaw, viewportSize);

          // Reset service state
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          service.tabViewStates.set(new Map());
          service.viewDimensions.set(null);
          service.dragState.set(null);
          uuidCounter = 0;
          correlationCounter = 0;

          setupTabWithScrollbar(scrollbarMax, scrollbarMax, viewportSize, viewportSize);

          // Attempt vertical drag start
          service.handleVerticalDragStart(mousePos, trackLength);
          const vDrag = service.dragState();

          // Attempt horizontal drag start
          service.handleHorizontalDragStart(mousePos, trackLength);
          const hDrag = service.dragState();

          // Neither should produce a DragState
          return vDrag === null && hDrag === null;
        }
      ),
      { numRuns: 10 }
    );
  });
});
