/** Position of the tab container relative to the text view area */
export type TabPosition = 'top' | 'bottom';

/** Represents a single open file tab */
export interface Tab {
  /** Unique identifier (crypto.randomUUID) */
  id: string;
  /** Full absolute file path (from backend response) */
  filePath: string;
  /** Display name — last segment of filePath */
  fileName: string;
  /** UUID from backend open-file response, keys all subsequent communication */
  viewSessionId: string;
}

/** Per-session view state tracked by ShellStateService */
export interface TabViewState {
  /** Whether a scan-complete notification has been received for this session */
  scanComplete: boolean;
  /** Cached rows from last successful get-view response */
  viewRows: string[] | null;
  /** Error message from last get-view response */
  errorMessage: string | null;
  /** Non-null while awaiting a get-view response */
  pendingCorrelationId: string | null;
  /** True if trigger fired before measurement was ready */
  deferred: boolean;
  /** Current scrollbar max values for this tab */
  scrollbarState: ScrollbarState;
  /** Zero-based index of the first visible line (vertical scroll position) */
  startLine: number;
  /** Zero-based index of the first visible column (horizontal scroll position) */
  startCol: number;
  /** Character offset within startLine for wrapped-mode scrolling (0 when wrap off) */
  characterOffset: number;
  /** Whether this tab needs a content refresh (set when wrap mode toggled while inactive) */
  needsRefresh: boolean;
  /** Backend-provided line numbers per visual row (parallel to viewRows); null until first response */
  gutterNumbers: (number | null)[] | null;
}

/** Transient state during scrollbar thumb drag */
export interface DragState {
  /** Which axis is being dragged */
  axis: 'vertical' | 'horizontal';
  /** Mouse coordinate at drag start (clientY for vertical, clientX for horizontal) */
  startMousePos: number;
  /** startLine or startCol value at drag start */
  startScrollPos: number;
  /** Track length in pixels (track element size minus thumb size) */
  trackLength: number;
  /** Scrollbar max value at drag start */
  scrollbarMax: number;
  /** Viewport size (rowCount or colCount) at drag start */
  viewportSize: number;
}

/** Computed viewport dimensions in character units */
export interface ViewDimensions {
  /** Number of text rows that fit vertically */
  rowCount: number;
  /** Number of text columns that fit horizontally */
  colCount: number;
}

/** Mirrors backend ScanState enum values */
export type ScanStateValue =
  | 'NotStarted'
  | 'QuickScanInProgress'
  | 'QuickScanComplete'
  | 'FullScanInProgress'
  | 'FullScanComplete'
  | 'Failed'
  | 'Cancelled';

/** Scrollbar dimension data from backend get-scroll-info response */
export interface ScrollInfo {
  /** Total line count from LineIndex */
  lineCount: number;
  /** Maximum byte_length across all discovered lines */
  maxByteLength: number;
  /** Maximum char_length across all lines with char_length computed (0 if none computed yet) */
  maxCharLength: number;
  /** Current scan state as reported by backend */
  scanState: ScanStateValue;
}

/** Computed scrollbar max values for a tab */
export interface ScrollbarState {
  /** Vertical scrollbar max = total line count */
  verticalMax: number;
  /** Horizontal scrollbar max = max byte_length or max char_length depending on scan state */
  horizontalMax: number;
  /** Whether scrollbars are disabled (zero values) */
  disabled: boolean;
}
