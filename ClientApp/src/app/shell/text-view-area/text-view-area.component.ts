import { AfterViewInit, Component, ElementRef, inject, OnDestroy, ViewChild } from '@angular/core';
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

  @ViewChild('verticalTrack') verticalTrack!: ElementRef<HTMLElement>;
  @ViewChild('horizontalTrack') horizontalTrack!: ElementRef<HTMLElement>;

  readonly activeTab = this.state.activeTab;
  readonly hasOpenTabs = this.state.hasOpenTabs;
  readonly viewRows = this.state.activeViewRows;
  readonly viewError = this.state.activeViewError;
  readonly isViewPending = this.state.isViewPending;
  readonly scrollbarState = this.state.activeScrollbarState;

  readonly verticalThumbRatio = this.state.verticalThumbRatio;
  readonly verticalThumbFraction = this.state.verticalThumbFraction;
  readonly horizontalThumbRatio = this.state.horizontalThumbRatio;
  readonly horizontalThumbFraction = this.state.horizontalThumbFraction;

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

  // --- Event handlers ---

  onWheel(event: WheelEvent): void {
    event.preventDefault();
    this.state.handleWheel(event.deltaY, event.deltaX);
  }

  onKeydown(event: KeyboardEvent): void {
    const arrowKeys = ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'];
    if (!arrowKeys.includes(event.key)) return;
    if (!this.state.activeTab()) return; // no active tab → don't preventDefault
    event.preventDefault();
    const directionMap: Record<string, 'up' | 'down' | 'left' | 'right'> = {
      ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right',
    };
    this.state.handleArrowKey(directionMap[event.key]);
  }

  onVerticalThumbMousedown(event: MouseEvent): void {
    event.preventDefault();
    const track = this.verticalTrack.nativeElement;
    const trackLength = track.clientHeight - this.computeVerticalThumbPx();
    document.body.style.userSelect = 'none';
    this.state.handleVerticalDragStart(event.clientY, trackLength);

    const onMouseMove = (e: MouseEvent) => this.state.handleDragMove(e.clientY);
    const onMouseUp = () => {
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
      document.body.style.userSelect = '';
      this.state.handleDragEnd();
    };
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }

  onHorizontalThumbMousedown(event: MouseEvent): void {
    event.preventDefault();
    const track = this.horizontalTrack.nativeElement;
    const trackLength = track.clientWidth - this.computeHorizontalThumbPx();
    document.body.style.userSelect = 'none';
    this.state.handleHorizontalDragStart(event.clientX, trackLength);

    const onMouseMove = (e: MouseEvent) => this.state.handleDragMove(e.clientX);
    const onMouseUp = () => {
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
      document.body.style.userSelect = '';
      this.state.handleDragEnd();
    };
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }

  // --- Thumb size/position computation ---

  computeVerticalThumbPx(): number {
    const track = this.verticalTrack?.nativeElement;
    if (!track) return 20;
    const ratio = this.verticalThumbRatio();
    return Math.max(20, ratio * track.clientHeight);
  }

  computeVerticalThumbTopPx(): number {
    const track = this.verticalTrack?.nativeElement;
    if (!track) return 0;
    const thumbPx = this.computeVerticalThumbPx();
    const availableTrack = track.clientHeight - thumbPx;
    return this.verticalThumbFraction() * availableTrack;
  }

  computeHorizontalThumbPx(): number {
    const track = this.horizontalTrack?.nativeElement;
    if (!track) return 20;
    const ratio = this.horizontalThumbRatio();
    return Math.max(20, ratio * track.clientWidth);
  }

  computeHorizontalThumbLeftPx(): number {
    const track = this.horizontalTrack?.nativeElement;
    if (!track) return 0;
    const thumbPx = this.computeHorizontalThumbPx();
    const availableTrack = track.clientWidth - thumbPx;
    return this.horizontalThumbFraction() * availableTrack;
  }
}
