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
