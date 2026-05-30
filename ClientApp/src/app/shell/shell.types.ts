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
}
