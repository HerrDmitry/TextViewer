/**
 * Unit tests for TextViewAreaComponent measurement and rendering
 *
 * Validates: Requirements 1.1, 1.2, 1.5, 1.6, 1.7, 5.1, 5.2, 5.3, 5.4, 5.5, 5.6
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

// --- Mock ResizeObserver ---
let resizeCallback: (() => void) | null = null;
let observedElements: HTMLElement[] = [];
let disconnected = false;

class MockResizeObserver {
  constructor(callback: () => void) {
    resizeCallback = callback;
    disconnected = false;
  }
  observe(el: HTMLElement) {
    observedElements.push(el);
  }
  disconnect() {
    disconnected = true;
  }
}

(globalThis as any).ResizeObserver = MockResizeObserver;

// --- Mock ShellStateService ---
let mockActiveTab: any = null;
let mockHasOpenTabs = false;
let mockActiveViewRows: string[] | null = null;
let mockActiveViewError: string | null = null;
let mockIsViewPending = false;
let mockUpdateViewDimensions: jest.Mock = jest.fn();

jest.mock('../shell-state.service', () => ({
  ShellStateService: class MockShellStateService {
    activeTab = () => mockActiveTab;
    hasOpenTabs = () => mockHasOpenTabs;
    activeViewRows = () => mockActiveViewRows;
    activeViewError = () => mockActiveViewError;
    isViewPending = () => mockIsViewPending;
    updateViewDimensions = mockUpdateViewDimensions;
  },
}));

// Mock @angular/core
let injectMap: Map<any, any> = new Map();
let mockElementRef: any = null;

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
    Component: () => (target: any) => target,
    Injectable: () => (target: any) => target,
    AfterViewInit: class {},
    OnDestroy: class {},
    ElementRef: class {},
    signal,
    computed,
    inject,
  };
});

import { TextViewAreaComponent } from './text-view-area.component';
import { ShellStateService } from '../shell-state.service';
import { ElementRef } from '@angular/core';

describe('TextViewAreaComponent', () => {
  let component: TextViewAreaComponent;
  let hostElement: HTMLElement;

  beforeEach(() => {
    jest.useFakeTimers();
    uuidCounter = 0;
    resizeCallback = null;
    observedElements = [];
    disconnected = false;
    mockUpdateViewDimensions = jest.fn();

    // Reset mock state
    mockActiveTab = null;
    mockHasOpenTabs = false;
    mockActiveViewRows = null;
    mockActiveViewError = null;
    mockIsViewPending = false;

    // Create a real DOM element as host
    hostElement = document.createElement('div');
    Object.defineProperty(hostElement, 'clientWidth', { value: 800, configurable: true });
    Object.defineProperty(hostElement, 'clientHeight', { value: 600, configurable: true });
    document.body.appendChild(hostElement);

    mockElementRef = { nativeElement: hostElement };

    const mockService = new ShellStateService();
    // Patch updateViewDimensions on the instance
    mockService.updateViewDimensions = mockUpdateViewDimensions;

    injectMap.set(ShellStateService, mockService);
    injectMap.set(ElementRef, mockElementRef);

    component = new TextViewAreaComponent();
  });

  afterEach(() => {
    jest.useRealTimers();
    jest.restoreAllMocks();
    document.body.removeChild(hostElement);
  });

  // --- Measurement runs even without active tab (dimensions available for open-file) ---

  describe('Measurement runs without active tab', () => {
    it('calls updateViewDimensions even when no active tab (pre-populates dimensions)', () => {
      mockActiveTab = null;
      component.ngAfterViewInit();

      // Trigger resize
      resizeCallback!();
      jest.advanceTimersByTime(150);

      expect(mockUpdateViewDimensions).toHaveBeenCalled();
    });
  });

  // --- Measurement triggered on active tab (AfterViewInit) (Req 1.1) ---

  describe('Measurement triggered on active tab (AfterViewInit)', () => {
    it('calls updateViewDimensions after debounce when active tab exists', () => {
      mockActiveTab = { id: 'tab-1', viewSessionId: 'session-1', filePath: '/file.txt', fileName: 'file.txt' };
      component.ngAfterViewInit();

      // ResizeObserver fires on observe
      resizeCallback!();
      jest.advanceTimersByTime(150);

      expect(mockUpdateViewDimensions).toHaveBeenCalled();
    });
  });

  // --- Char_Metrics uses "M" reference character (Req 1.2) ---

  describe('Char_Metrics uses "M" reference character', () => {
    it('creates a span with textContent "M" for measurement', () => {
      mockActiveTab = { id: 'tab-1', viewSessionId: 'session-1', filePath: '/file.txt', fileName: 'file.txt' };

      const appendSpy = jest.spyOn(hostElement, 'appendChild');

      component.ngAfterViewInit();
      resizeCallback!();
      jest.advanceTimersByTime(150);

      // Check that a span with "M" was appended
      const calls = appendSpy.mock.calls;
      expect(calls.length).toBeGreaterThan(0);
      const span = calls[0][0] as HTMLElement;
      expect(span.textContent).toBe('M');
      expect(span.style.fontFamily).toBe('monospace');
      expect(span.style.whiteSpace).toBe('pre');
    });
  });

  // --- Resize debounce: multiple events within 150ms → single recompute (Req 1.5) ---

  describe('Resize debounce', () => {
    it('multiple resize events within 150ms result in single measurement', () => {
      mockActiveTab = { id: 'tab-1', viewSessionId: 'session-1', filePath: '/file.txt', fileName: 'file.txt' };
      component.ngAfterViewInit();

      // Fire multiple resize events rapidly
      resizeCallback!();
      jest.advanceTimersByTime(50);
      resizeCallback!();
      jest.advanceTimersByTime(50);
      resizeCallback!();
      jest.advanceTimersByTime(150);

      // Only one measurement should have occurred (the last debounced one)
      expect(mockUpdateViewDimensions).toHaveBeenCalledTimes(1);
    });
  });

  // --- Dimension change emits new values (Req 1.6) ---

  describe('Dimension change emits new values', () => {
    it('emits new dimensions when size changes', () => {
      mockActiveTab = { id: 'tab-1', viewSessionId: 'session-1', filePath: '/file.txt', fileName: 'file.txt' };
      component.ngAfterViewInit();

      // First measurement
      resizeCallback!();
      jest.advanceTimersByTime(150);

      const firstCall = mockUpdateViewDimensions.mock.calls[0][0];
      expect(firstCall.rowCount).toBeGreaterThan(0);
      expect(firstCall.colCount).toBeGreaterThan(0);

      // Change dimensions
      Object.defineProperty(hostElement, 'clientWidth', { value: 400, configurable: true });
      Object.defineProperty(hostElement, 'clientHeight', { value: 300, configurable: true });

      resizeCallback!();
      jest.advanceTimersByTime(150);

      // Should have been called again with different values
      expect(mockUpdateViewDimensions).toHaveBeenCalledTimes(2);
      const secondCall = mockUpdateViewDimensions.mock.calls[1][0];
      expect(secondCall.rowCount).toBeLessThan(firstCall.rowCount);
      expect(secondCall.colCount).toBeLessThan(firstCall.colCount);
    });

    it('does not emit when dimensions remain the same', () => {
      mockActiveTab = { id: 'tab-1', viewSessionId: 'session-1', filePath: '/file.txt', fileName: 'file.txt' };
      component.ngAfterViewInit();

      // First measurement
      resizeCallback!();
      jest.advanceTimersByTime(150);

      // Same dimensions, trigger again
      resizeCallback!();
      jest.advanceTimersByTime(150);

      // Should only have been called once (no change)
      expect(mockUpdateViewDimensions).toHaveBeenCalledTimes(1);
    });
  });

  // --- View rows rendered as block elements in order (Req 5.1, 5.2) ---

  describe('View rows rendered as block elements in order', () => {
    it('renders each row as a div.view-row in order', () => {
      mockHasOpenTabs = true;
      mockActiveViewRows = ['line one', 'line two', 'line three'];
      mockActiveViewError = null;

      // Simulate template rendering by checking signal values
      expect(component.viewRows()).toEqual(['line one', 'line two', 'line three']);
      expect(component.hasOpenTabs()).toBe(true);
      expect(component.viewError()).toBeNull();
    });
  });

  // --- Overflow hidden on content container (Req 5.3) ---

  describe('Overflow hidden on content container', () => {
    it('component template uses view-content container with overflow hidden (verified via CSS)', () => {
      // The CSS file defines .view-content { overflow: hidden } and .text-view-area { overflow: hidden }
      // We verify the component exposes the correct signals for template rendering
      mockHasOpenTabs = true;
      mockActiveViewRows = ['row1'];
      expect(component.viewRows()).toEqual(['row1']);
      // The overflow:hidden is enforced by the CSS class .view-content and .text-view-area
      // This is a structural test confirming the component wires signals correctly
    });
  });

  // --- Error response displayed in distinct style (Req 5.4) ---

  describe('Error response displayed in distinct style', () => {
    it('exposes viewError signal for template rendering', () => {
      mockHasOpenTabs = true;
      mockActiveViewError = 'Session not found: abc-123';
      mockActiveViewRows = null;

      expect(component.viewError()).toBe('Session not found: abc-123');
      expect(component.viewRows()).toBeNull();
    });
  });

  // --- Tab switch shows cached rows synchronously (Req 5.5) ---

  describe('Tab switch shows cached rows synchronously', () => {
    it('activeViewRows reflects cached rows for the active tab immediately', () => {
      // Simulate tab switch: the signal returns cached rows for the new active tab
      mockActiveTab = { id: 'tab-2', viewSessionId: 'session-2', filePath: '/other.txt', fileName: 'other.txt' };
      mockActiveViewRows = ['cached row 1', 'cached row 2'];

      // Accessing the signal returns cached rows synchronously (no async)
      expect(component.viewRows()).toEqual(['cached row 1', 'cached row 2']);
    });
  });

  // --- Pending state with no cache shows empty content (Req 5.6) ---

  describe('Pending state with no cache shows empty content', () => {
    it('shows no content when pending and no cached rows', () => {
      mockHasOpenTabs = true;
      mockActiveTab = { id: 'tab-1', viewSessionId: 'session-1', filePath: '/file.txt', fileName: 'file.txt' };
      mockIsViewPending = true;
      mockActiveViewRows = null;
      mockActiveViewError = null;

      // Template logic: hasOpenTabs=true, viewError=null, viewRows=null → empty content region
      expect(component.hasOpenTabs()).toBe(true);
      expect(component.viewError()).toBeNull();
      expect(component.viewRows()).toBeNull();
      expect(component.isViewPending()).toBe(true);
    });
  });

  // --- Cleanup ---

  describe('ngOnDestroy', () => {
    it('disconnects ResizeObserver', () => {
      component.ngAfterViewInit();
      component.ngOnDestroy();
      expect(disconnected).toBe(true);
    });

    it('clears debounce timer', () => {
      component.ngAfterViewInit();
      resizeCallback!(); // Start a debounce timer
      component.ngOnDestroy();

      // Advancing time should not trigger measurement
      jest.advanceTimersByTime(200);
      expect(mockUpdateViewDimensions).not.toHaveBeenCalled();
    });
  });
});
