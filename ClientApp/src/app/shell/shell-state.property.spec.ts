/**
 * Feature: viewer-ui-shell, Property 7: Exactly one active tab when tabs are non-empty
 *
 * Validates: Requirements 6.4
 *
 * Property: For any sequence of operations (open file, close tab, activate tab)
 * applied to the ShellStateService, if the tabs array is non-empty after the
 * operation, then activeTabId shall reference exactly one tab present in the
 * tabs array.
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
let mockSubscribeHandler: ((msg: any) => void) | null = null;
let mockSubscribeHandlers: Map<string, (msg: any) => void> = new Map();

jest.mock('../services/message-bus-client.service', () => ({
  MessageBusClient: class MockMessageBusClient {
    send = (...args: any[]) => mockSend(...args);
    configure = jest.fn();
    subscribe = (messageType: string, handler: (msg: any) => void) => {
      mockSubscribeHandlers.set(messageType, handler);
      if (messageType === 'open-file') {
        mockSubscribeHandler = handler;
      }
      return { unsubscribe: jest.fn() };
    };
  },
}));

// Mock @angular/core to provide signal, computed, inject
let injectMap: Map<any, any> = new Map();

jest.mock('@angular/core', () => {
  // Simple signal implementation
  function signal<T>(initialValue: T) {
    let value = initialValue;
    const fn = () => value;
    fn.set = (v: T) => { value = v; };
    fn.update = (updater: (v: T) => T) => { value = updater(value); };
    return fn;
  }

  // Simple computed implementation
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
import { ShellStateService } from './shell-state.service';
import { MessageBusClient } from '../services/message-bus-client.service';
import { InboundMessage } from '../services/message-bus.types';

interface OpenFileOp {
  kind: 'openFile';
  filePath: string;
}

interface CloseTabOp {
  kind: 'closeTab';
  tabIndex: number;
}

interface ActivateTabOp {
  kind: 'activateTab';
  tabIndex: number;
}

type Operation = OpenFileOp | CloseTabOp | ActivateTabOp;

describe('Feature: viewer-ui-shell, Property 7: Exactly one active tab when tabs are non-empty', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    correlationCounter = 0;
    mockSubscribeHandler = null;
    mockSubscribeHandlers = new Map();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    // Mock localStorage
    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    // Set up inject map so ShellStateService gets the mock MessageBusClient
    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  /**
   * Simulate an open-file operation: trigger the request,
   * then deliver a non-empty response to create a tab.
   */
  function simulateOpenFile(filePath: string): void {
    service.triggerOpenFile();
    const corrId = `corr-${correlationCounter}`;
    if (mockSubscribeHandler) {
      mockSubscribeHandler({
        messageType: 'open-file',
        correlationId: corrId,
        payload: filePath,
      } as InboundMessage);
    }
  }

  // Generator for operations
  const operationArb: fc.Arbitrary<Operation> = fc.oneof(
    fc.string({
      minLength: 1,
      maxLength: 20,
      unit: fc.constantFrom(...'abcdefghijklmnopqrstuvwxyz0123456789/\\.'.split('')),
    }).map((filePath): OpenFileOp => ({ kind: 'openFile', filePath })),
    fc.nat({ max: 99 }).map((tabIndex): CloseTabOp => ({ kind: 'closeTab', tabIndex })),
    fc.nat({ max: 99 }).map((tabIndex): ActivateTabOp => ({ kind: 'activateTab', tabIndex }))
  );

  it('activeTabId references a tab in the tabs array whenever tabs are non-empty', () => {
    fc.assert(
      fc.property(
        fc.array(operationArb, { minLength: 1, maxLength: 20 }),
        (operations: Operation[]) => {
          // Reset service state for each property run
          service.tabs.set([]);
          service.activeTabId.set(null);
          service.pendingCorrelationId.set(null);
          correlationCounter = 0;

          for (const op of operations) {
            const currentTabs = service.tabs();

            switch (op.kind) {
              case 'openFile':
                simulateOpenFile(op.filePath);
                break;

              case 'closeTab':
                if (currentTabs.length > 0) {
                  const idx = op.tabIndex % currentTabs.length;
                  service.closeTab(currentTabs[idx].id);
                }
                break;

              case 'activateTab':
                if (currentTabs.length > 0) {
                  const idx = op.tabIndex % currentTabs.length;
                  service.activateTab(currentTabs[idx].id);
                }
                break;
            }

            // Invariant: if tabs non-empty, activeTabId must reference a tab in the array
            const tabsAfter = service.tabs();
            if (tabsAfter.length > 0) {
              const activeId = service.activeTabId();
              const found = tabsAfter.some(t => t.id === activeId);
              if (!found) {
                return false;
              }
            }
          }

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});


/**
 * Feature: viewer-ui-shell, Property 4: Empty response preserves tab state
 *
 * Validates: Requirements 7.3
 *
 * Property: For any existing tab state (including empty), when an open-file
 * response with an empty payload is received, the tabs array and activeTabId
 * shall remain unchanged.
 */

// Characters valid in path segments
const segmentChars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-';

/** Generator for a single path segment */
const pathSegment = fc.string({
  minLength: 1,
  maxLength: 10,
  unit: fc.constantFrom(...segmentChars.split('')),
});

/** Generator for a file path with separators */
const filePathArb = fc.tuple(
  fc.array(pathSegment, { minLength: 1, maxLength: 4 }),
  fc.constantFrom('/', '\\'),
  pathSegment,
).map(([segments, sep, name]) => segments.join(sep) + sep + name);

/** Generator for a Tab */
const tabArb = filePathArb.map((fp) => ({
  id: crypto.randomUUID(),
  filePath: fp,
  fileName: fp.substring(Math.max(fp.lastIndexOf('/'), fp.lastIndexOf('\\')) + 1),
  viewSessionId: crypto.randomUUID(),
}));

/** Generator for a random tab state: 0-5 tabs with a valid activeTabId */
const tabStateArb = fc.array(tabArb, { minLength: 0, maxLength: 5 }).chain(tabs => {
  if (tabs.length === 0) {
    return fc.constant({ tabs, activeTabId: null as string | null });
  }
  return fc.integer({ min: 0, max: tabs.length - 1 }).map(idx => ({
    tabs,
    activeTabId: tabs[idx].id,
  }));
});

describe('Feature: viewer-ui-shell, Property 4: Empty response preserves tab state', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    correlationCounter = 0;
    mockSubscribeHandler = null;
    mockSubscribeHandlers = new Map();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    // Mock localStorage
    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    // Set up inject map so ShellStateService gets the mock MessageBusClient
    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('empty response does not modify tabs array or activeTabId', () => {
    fc.assert(
      fc.property(tabStateArb, ({ tabs: initialTabs, activeTabId: initialActiveTabId }) => {
        // Set up initial tab state
        service.tabs.set([...initialTabs]);
        service.activeTabId.set(initialActiveTabId);

        // Snapshot state before
        const tabsBefore = service.tabs().map((t: any) => ({ ...t }));
        const activeTabIdBefore = service.activeTabId();

        // Trigger open file to set pending correlation ID
        service.triggerOpenFile();

        // Simulate empty response (user cancelled dialog)
        const corrId = `corr-${correlationCounter}`;
        if (mockSubscribeHandler) {
          mockSubscribeHandler({
            messageType: 'open-file',
            correlationId: corrId,
            payload: '',
          } as InboundMessage);
        }

        // Assert tabs unchanged
        const tabsAfter = service.tabs();
        if (tabsAfter.length !== tabsBefore.length) return false;
        for (let i = 0; i < tabsBefore.length; i++) {
          if (tabsAfter[i].id !== tabsBefore[i].id) return false;
          if (tabsAfter[i].filePath !== tabsBefore[i].filePath) return false;
          if (tabsAfter[i].fileName !== tabsBefore[i].fileName) return false;
        }

        // Assert activeTabId unchanged
        return service.activeTabId() === activeTabIdBefore;
      }),
      { numRuns: 10 }
    );
  });
});

/**
 * Feature: viewer-ui-shell, Property 6: Active file path reflects active tab
 *
 * Validates: Requirements 6.1, 6.3
 *
 * Property: For any non-empty tab array and any valid activeTabId pointing to a
 * tab in that array, the computed activeFilePath shall equal that tab's filePath.
 * When activeTabId is null or tabs is empty, activeFilePath shall be the empty string.
 */

/** Generator for a non-empty tab array with a selected active index */
const nonEmptyTabsWithActive = fc.array(tabArb, { minLength: 1, maxLength: 5 }).chain(tabs =>
  fc.tuple(fc.constant(tabs), fc.integer({ min: 0, max: tabs.length - 1 }))
);

describe('Feature: viewer-ui-shell, Property 6: Active file path reflects active tab', () => {
  let service: ShellStateService;

  beforeEach(() => {
    mockSubscribeHandler = null;
    mockSubscribeHandlers = new Map();
    mockSend = jest.fn(() => 'corr-1');

    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('activeFilePath equals the active tab filePath when activeTabId points to a valid tab', () => {
    fc.assert(
      fc.property(nonEmptyTabsWithActive, ([tabs, activeIndex]) => {
        service.tabs.set(tabs);
        service.activeTabId.set(tabs[activeIndex].id);

        return service.activeFilePath() === tabs[activeIndex].filePath;
      }),
      { numRuns: 10 }
    );
  });

  it('activeFilePath is empty string when activeTabId is null', () => {
    fc.assert(
      fc.property(
        fc.oneof(
          fc.constant([] as any[]),
          fc.array(tabArb, { minLength: 1, maxLength: 5 })
        ),
        (tabs) => {
          service.tabs.set(tabs);
          service.activeTabId.set(null);

          return service.activeFilePath() === '';
        }
      ),
      { numRuns: 10 }
    );
  });

  it('activeFilePath is empty string when tabs is empty', () => {
    fc.assert(
      fc.property(
        fc.option(fc.uuid(), { nil: null }),
        (activeId) => {
          service.tabs.set([]);
          service.activeTabId.set(activeId);

          return service.activeFilePath() === '';
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Property 8: Position change preserves tab state
 *
 * Validates: Requirements 4.3
 *
 * For any tab state (tabs array, activeTabId) and any position change
 * (top→bottom or bottom→top), the tabs array contents, their order,
 * and activeTabId shall remain identical after the position change.
 */
describe('Feature: viewer-ui-shell, Property 8: Position change preserves tab state', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    correlationCounter = 0;
    mockSubscribeHandler = null;
    mockSubscribeHandlers = new Map();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    // Mock localStorage
    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    // Set up inject map so ShellStateService gets the mock MessageBusClient
    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  // Generator for a random Tab using the same pattern as other properties in this file
  const prop8TabArb = filePathArb.map((fp) => ({
    id: crypto.randomUUID(),
    filePath: fp,
    fileName: fp.substring(Math.max(fp.lastIndexOf('/'), fp.lastIndexOf('\\')) + 1),
    viewSessionId: crypto.randomUUID(),
  }));

  // Generator for a random tab state: array of tabs + activeTabId from one of them (or null if empty)
  const prop8TabStateArb = fc
    .array(prop8TabArb, { minLength: 0, maxLength: 10 })
    .chain(tabs => {
      if (tabs.length === 0) {
        return fc.constant({ tabs, activeTabId: null as string | null });
      }
      return fc.integer({ min: 0, max: tabs.length - 1 }).map(idx => ({
        tabs,
        activeTabId: tabs[idx].id,
      }));
    });

  // Generator for position change direction
  const positionChangeArb = fc.oneof(
    fc.constant<{ from: 'top' | 'bottom'; to: 'top' | 'bottom' }>({ from: 'top', to: 'bottom' }),
    fc.constant<{ from: 'top' | 'bottom'; to: 'top' | 'bottom' }>({ from: 'bottom', to: 'top' })
  );

  it('tabs array contents, order, and activeTabId remain identical after position change', () => {
    fc.assert(
      fc.property(
        prop8TabStateArb,
        positionChangeArb,
        (tabState, posChange) => {
          // Set up initial state
          service.tabs.set(tabState.tabs);
          service.activeTabId.set(tabState.activeTabId);
          service.setTabPosition(posChange.from);

          // Capture state before position change
          const tabsBefore = [...service.tabs()];
          const activeTabIdBefore = service.activeTabId();

          // Perform position change
          service.setTabPosition(posChange.to);

          // Assert tabs array unchanged
          const tabsAfter = service.tabs();
          const activeTabIdAfter = service.activeTabId();

          // Same length
          if (tabsAfter.length !== tabsBefore.length) return false;

          // Same contents and order
          for (let i = 0; i < tabsBefore.length; i++) {
            if (tabsAfter[i].id !== tabsBefore[i].id) return false;
            if (tabsAfter[i].filePath !== tabsBefore[i].filePath) return false;
            if (tabsAfter[i].fileName !== tabsBefore[i].fileName) return false;
          }

          // Same activeTabId
          if (activeTabIdAfter !== activeTabIdBefore) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Feature: viewer-ui-shell, Property 5: Close tab removes it and selects correct adjacent
 *
 * Validates: Requirements 3.5, 3.6, 3.7, 3.8
 *
 * Property: For any tab array with N ≥ 1 tabs and any tab closed:
 * - The closed tab shall no longer appear in the tabs array (length decreases by one).
 * - If the closed tab was the active tab and tabs remain, the new active tab shall be
 *   the right neighbor (index + 1) if it exists, otherwise the left neighbor (index - 1).
 * - If the closed tab was not the active tab, activeTabId shall remain unchanged.
 * - If the closed tab was the last tab, activeTabId shall become null.
 */
describe('Feature: viewer-ui-shell, Property 5: Close tab removes it and selects correct adjacent', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    correlationCounter = 0;
    mockSubscribeHandler = null;
    mockSubscribeHandlers = new Map();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    // Mock localStorage
    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    // Set up inject map so ShellStateService gets the mock MessageBusClient
    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  /** Generator for a tab array of 1-10 tabs with unique ids */
  const tabArrayGen = fc.integer({ min: 1, max: 10 }).chain(n =>
    fc.tuple(
      ...Array.from({ length: n }, (_, i) =>
        fc.constant({
          id: `tab-${i}`,
          filePath: `/path/to/file-${i}.txt`,
          fileName: `file-${i}.txt`,
          viewSessionId: `session-${i}`,
        })
      )
    )
  );

  it('closed tab is removed and length decreases by one', () => {
    fc.assert(
      fc.property(
        tabArrayGen.chain(tabs =>
          fc.record({
            tabs: fc.constant(tabs),
            closeIndex: fc.integer({ min: 0, max: tabs.length - 1 }),
            activeIndex: fc.integer({ min: 0, max: tabs.length - 1 }),
          })
        ),
        ({ tabs, closeIndex, activeIndex }) => {
          service.tabs.set(tabs);
          service.activeTabId.set(tabs[activeIndex].id);

          const tabToClose = tabs[closeIndex];
          service.closeTab(tabToClose.id);

          const remaining = service.tabs();
          // Length decreases by one
          if (remaining.length !== tabs.length - 1) return false;
          // Closed tab no longer present
          if (remaining.some(t => t.id === tabToClose.id)) return false;
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('closing the active tab selects right neighbor if it exists, otherwise left', () => {
    fc.assert(
      fc.property(
        tabArrayGen
          .filter(tabs => tabs.length >= 2)
          .chain(tabs =>
            fc.record({
              tabs: fc.constant(tabs),
              closeIndex: fc.integer({ min: 0, max: tabs.length - 1 }),
            })
          ),
        ({ tabs, closeIndex }) => {
          // Set the tab to close as the active tab
          service.tabs.set(tabs);
          service.activeTabId.set(tabs[closeIndex].id);

          service.closeTab(tabs[closeIndex].id);

          const newActiveId = service.activeTabId();

          // Right neighbor exists if closeIndex < original length - 1
          if (closeIndex < tabs.length - 1) {
            // Right neighbor: the tab that was at closeIndex + 1 in original array
            if (newActiveId !== tabs[closeIndex + 1].id) return false;
          } else {
            // No right neighbor, fall back to left neighbor
            if (newActiveId !== tabs[closeIndex - 1].id) return false;
          }
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('closing a non-active tab does not change activeTabId', () => {
    fc.assert(
      fc.property(
        tabArrayGen
          .filter(tabs => tabs.length >= 2)
          .chain(tabs =>
            fc.record({
              tabs: fc.constant(tabs),
              activeIndex: fc.integer({ min: 0, max: tabs.length - 1 }),
            }).chain(({ tabs, activeIndex }) =>
              fc.record({
                tabs: fc.constant(tabs),
                activeIndex: fc.constant(activeIndex),
                closeIndex: fc.integer({ min: 0, max: tabs.length - 1 }).filter(i => i !== activeIndex),
              })
            )
          ),
        ({ tabs, activeIndex, closeIndex }) => {
          service.tabs.set(tabs);
          service.activeTabId.set(tabs[activeIndex].id);

          service.closeTab(tabs[closeIndex].id);

          // activeTabId unchanged
          if (service.activeTabId() !== tabs[activeIndex].id) return false;
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('closing the last remaining tab sets activeTabId to null', () => {
    fc.assert(
      fc.property(
        fc.constant({ id: 'tab-0', filePath: '/path/to/file.txt', fileName: 'file.txt', viewSessionId: 'session-0' }),
        (tab) => {
          service.tabs.set([tab]);
          service.activeTabId.set(tab.id);

          service.closeTab(tab.id);

          if (service.tabs().length !== 0) return false;
          if (service.activeTabId() !== null) return false;
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});


/**
 * Feature: viewer-ui-shell, Property 3: Opening a file creates a tab and makes it active
 *
 * Validates: Requirements 3.1, 3.2, 3.4, 7.2
 *
 * Property: For any non-empty file path received as a response, the ShellStateService
 * shall append a new tab to the tabs array (increasing length by one), set that tab's
 * filePath to the received path, set its fileName to the last path segment, and set
 * activeTabId to the new tab's ID.
 */
describe('Feature: viewer-ui-shell, Property 3: Opening a file creates a tab and makes it active', () => {
  let service: ShellStateService;
  let correlationCounter: number;

  beforeEach(() => {
    correlationCounter = 0;
    mockSubscribeHandler = null;
    mockSubscribeHandlers = new Map();
    mockSend = jest.fn(() => `corr-${++correlationCounter}`);

    // Mock localStorage
    jest.spyOn(Storage.prototype, 'getItem').mockReturnValue(null);
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {});

    // Set up inject map so ShellStateService gets the mock MessageBusClient
    const mockBus = new MessageBusClient();
    injectMap.set(MessageBusClient, mockBus);

    service = new ShellStateService();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  // Generator for path segments (non-empty, alphanumeric + dot/dash/underscore)
  const prop3SegmentChars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-';
  const prop3Segment = fc.string({
    minLength: 1,
    maxLength: 10,
    unit: fc.constantFrom(...prop3SegmentChars.split('')),
  });

  // Generator for a file path with at least one separator
  const prop3FilePathArb = fc.tuple(
    fc.array(prop3Segment, { minLength: 1, maxLength: 4 }),
    fc.constantFrom('/', '\\'),
    prop3Segment,
  ).map(([segments, sep, fileName]) => segments.join(sep) + sep + fileName);

  it('opening a file appends a tab with correct filePath, fileName, and sets activeTabId', () => {
    fc.assert(
      fc.property(prop3FilePathArb, (filePath: string) => {
        // Reset state for each run
        service.tabs.set([]);
        service.activeTabId.set(null);
        service.pendingCorrelationId.set(null);
        correlationCounter = 0;

        const tabsBefore = service.tabs().length;

        // Trigger open file to set pending state
        service.triggerOpenFile();

        // Simulate non-empty response with the generated path
        const corrId = `corr-${correlationCounter}`;
        if (mockSubscribeHandler) {
          mockSubscribeHandler({
            messageType: 'open-file',
            correlationId: corrId,
            payload: filePath,
          } as InboundMessage);
        }

        const tabsAfter = service.tabs();

        // Tabs length increased by 1
        if (tabsAfter.length !== tabsBefore + 1) return false;

        // New tab has correct filePath
        const newTab = tabsAfter[tabsAfter.length - 1];
        if (newTab.filePath !== filePath) return false;

        // fileName is the last path segment
        const lastSep = Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\'));
        const expectedFileName = lastSep === -1 ? filePath : filePath.substring(lastSep + 1);
        if (newTab.fileName !== expectedFileName) return false;

        // activeTabId points to the new tab
        if (service.activeTabId() !== newTab.id) return false;

        return true;
      }),
      { numRuns: 10 }
    );
  });
});
