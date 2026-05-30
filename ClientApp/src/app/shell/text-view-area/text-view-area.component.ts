import { AfterViewInit, Component, ElementRef, inject, OnDestroy } from '@angular/core';
import { ShellStateService } from '../shell-state.service';
import { ViewDimensions } from '../shell.types';

@Component({
  selector: 'app-text-view-area',
  standalone: true,
  templateUrl: './text-view-area.component.html',
  styleUrl: './text-view-area.component.css'
})
export class TextViewAreaComponent implements AfterViewInit, OnDestroy {
  private readonly state = inject(ShellStateService);
  private readonly el = inject(ElementRef);

  readonly activeTab = this.state.activeTab;
  readonly hasOpenTabs = this.state.hasOpenTabs;
  readonly viewRows = this.state.activeViewRows;
  readonly viewError = this.state.activeViewError;
  readonly isViewPending = this.state.isViewPending;

  private resizeObserver: ResizeObserver | null = null;
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private lastDimensions: ViewDimensions | null = null;

  ngAfterViewInit(): void {
    this.setupResizeObserver();
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }
  }

  private setupResizeObserver(): void {
    const host = this.el.nativeElement as HTMLElement;
    this.resizeObserver = new ResizeObserver(() => {
      if (this.debounceTimer) {
        clearTimeout(this.debounceTimer);
      }
      this.debounceTimer = setTimeout(() => {
        this.debounceTimer = null;
        this.measure();
      }, 150);
    });
    this.resizeObserver.observe(host);
  }

  private measure(): void {
    const host = this.el.nativeElement as HTMLElement;
    const pixelWidth = host.clientWidth;
    const pixelHeight = host.clientHeight;

    // Skip if element has no size yet (not laid out)
    if (pixelWidth === 0 || pixelHeight === 0) return;

    const charMetrics = this.computeCharMetrics();
    const rowCount = Math.max(1, Math.floor(pixelHeight / charMetrics.height));
    const colCount = Math.max(1, Math.floor(pixelWidth / charMetrics.width));

    const dims: ViewDimensions = { rowCount, colCount };

    // Only call updateViewDimensions if dimensions actually changed
    if (
      !this.lastDimensions ||
      this.lastDimensions.rowCount !== dims.rowCount ||
      this.lastDimensions.colCount !== dims.colCount
    ) {
      this.lastDimensions = dims;
      this.state.updateViewDimensions(dims);
    }
  }

  private computeCharMetrics(): { width: number; height: number } {
    const host = this.el.nativeElement as HTMLElement;

    // Create off-screen span with same font as .view-row
    const span = document.createElement('span');
    span.style.position = 'absolute';
    span.style.visibility = 'hidden';
    span.style.whiteSpace = 'pre';
    span.style.fontFamily = 'monospace';
    span.style.fontSize = '14px';
    span.style.lineHeight = 'normal';
    span.textContent = 'M';

    host.appendChild(span);
    const rect = span.getBoundingClientRect();
    host.removeChild(span);

    const width = rect.width || 8;   // fallback 8px if measurement returns 0
    const height = rect.height || 16; // fallback 16px if measurement returns 0

    return { width, height };
  }
}
