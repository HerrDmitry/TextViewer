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
}

/** Computed viewport dimensions in character units */
export interface ViewDimensions {
  /** Number of text rows that fit vertically */
  rowCount: number;
  /** Number of text columns that fit horizontally */
  colCount: number;
}
