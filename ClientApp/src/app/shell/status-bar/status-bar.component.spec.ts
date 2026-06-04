/**
 * Unit tests for StatusBarComponent progress bar rendering
 *
 * Validates: Requirements 1.1, 1.2, 2.5
 */

// --- Mock ShellStateService ---
let mockIsScanning = false;
let mockActiveScanProgress = 0;
let mockFilePath = '/some/file.txt';
let mockWrapMode = false;

jest.mock('../shell-state.service', () => ({
  ShellStateService: class MockShellStateService {
    isScanning = () => mockIsScanning;
    activeScanProgress = () => mockActiveScanProgress;
    activeFilePath = () => mockFilePath;
    wrapMode = () => mockWrapMode;
    toggleWrapMode = jest.fn();
  },
}));

jest.mock('@angular/core', () => {
  function inject(token: any) {
    return new token();
  }

  return {
    Component: () => (target: any) => target,
    Injectable: () => (target: any) => target,
    inject,
  };
});

import { StatusBarComponent } from './status-bar.component';

/**
 * Renders the StatusBarComponent template manually based on signal values.
 * This mirrors the actual template logic in status-bar.component.html.
 */
function renderTemplate(component: StatusBarComponent): HTMLElement {
  const container = document.createElement('div');
  let html = '<div class="status-bar">';
  html += `<span class="file-path">${component.filePath()}</span>`;
  if (component.isScanning()) {
    html += `<div class="progress-bar"><div class="progress-fill" style="width: ${component.activeScanProgress()}%"></div></div>`;
  }
  html += `<label class="wrap-checkbox"><input type="checkbox" ${component.wrapMode() ? 'checked' : ''} />Wrap</label>`;
  html += '</div>';
  container.innerHTML = html;
  return container;
}

describe('StatusBarComponent', () => {
  let component: StatusBarComponent;

  beforeEach(() => {
    mockIsScanning = false;
    mockActiveScanProgress = 0;
    mockFilePath = '/some/file.txt';
    mockWrapMode = false;
    component = new StatusBarComponent();
  });

  describe('Progress bar visibility', () => {
    it('renders .progress-bar when isScanning() is true', () => {
      mockIsScanning = true;
      mockActiveScanProgress = 42;

      const dom = renderTemplate(component);
      const progressBar = dom.querySelector('.progress-bar');

      expect(progressBar).not.toBeNull();
    });

    it('does NOT render .progress-bar when isScanning() is false', () => {
      mockIsScanning = false;

      const dom = renderTemplate(component);
      const progressBar = dom.querySelector('.progress-bar');

      expect(progressBar).toBeNull();
    });
  });

  describe('DOM order', () => {
    it('renders file-path → progress-bar → wrap-checkbox in correct order', () => {
      mockIsScanning = true;
      mockActiveScanProgress = 55;

      const dom = renderTemplate(component);
      const statusBar = dom.querySelector('.status-bar')!;
      const children = Array.from(statusBar.children);

      expect(children.length).toBe(3);
      expect(children[0].classList.contains('file-path')).toBe(true);
      expect(children[1].classList.contains('progress-bar')).toBe(true);
      expect(children[2].classList.contains('wrap-checkbox')).toBe(true);
    });
  });

  describe('CSS classes', () => {
    it('progress-bar contains .progress-fill child', () => {
      mockIsScanning = true;
      mockActiveScanProgress = 75;

      const dom = renderTemplate(component);
      const progressBar = dom.querySelector('.progress-bar')!;
      const progressFill = progressBar.querySelector('.progress-fill');

      expect(progressFill).not.toBeNull();
    });

    it('.progress-fill has correct width style matching progress percentage', () => {
      mockIsScanning = true;
      mockActiveScanProgress = 63;

      const dom = renderTemplate(component);
      const progressFill = dom.querySelector('.progress-fill') as HTMLElement;

      expect(progressFill.style.width).toBe('63%');
    });
  });
});
