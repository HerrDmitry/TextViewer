/**
 * Computes gutter width in pixels.
 * @param totalLogicalLines Total line count for the file
 * @param charWidth Width of a single monospace character in pixels
 * @returns Gutter width in pixels (digits * charWidth + 16px padding)
 */
export function computeGutterWidth(
  totalLogicalLines: number,
  charWidth: number
): number {
  if (totalLogicalLines <= 0) return 0;
  const digits = Math.max(1, Math.floor(Math.log10(totalLogicalLines)) + 1);
  return digits * charWidth + 16;
}

/**
 * Computes line numbers for non-wrapped mode.
 * @param startLine Zero-based first visible line
 * @param rowCount Number of rows returned from backend
 * @returns Array of 1-based line numbers
 */
export function computeNonWrappedLineNumbers(
  startLine: number,
  rowCount: number
): number[] {
  return Array.from({ length: rowCount }, (_, i) => startLine + i + 1);
}

/**
 * Computes Col_Count accounting for gutter width.
 * @param availablePixelWidth Total pixel width of the text-view-area host
 * @param gutterWidth Gutter element's client width (0 if not rendered)
 * @param charWidth Width of a single monospace character in pixels
 * @returns Column count (minimum 1)
 */
export function computeColCount(
  availablePixelWidth: number,
  gutterWidth: number,
  charWidth: number
): number {
  return Math.max(1, Math.floor((availablePixelWidth - gutterWidth) / charWidth));
}

/**
 * Splits response content into visual rows for wrapped-mode display.
 * Breaks at Col_Count character boundaries (hard wrap). Newline delimiters
 * (\n, \r\n, \r) are consumed as line-boundary markers — they end the current
 * row and start a new logical line without being counted toward Col_Count or
 * rendered as visible characters.
 *
 * @param content The response content string from the backend
 * @param colCount Maximum characters per visual row
 * @returns Array of visual row strings
 */
export function splitIntoVisualRows(content: string, colCount: number): string[] {
  if (content.length === 0) return [];
  const rows: string[] = [];
  let current = '';
  for (let i = 0; i < content.length; i++) {
    const ch = content[i];
    if (ch === '\n') {
      rows.push(current);
      current = '';
    } else if (ch === '\r') {
      // Handle \r\n as single delimiter
      if (i + 1 < content.length && content[i + 1] === '\n') i++;
      rows.push(current);
      current = '';
    } else {
      current += ch;
      if (current.length === colCount) {
        rows.push(current);
        current = '';
      }
    }
  }
  if (current.length > 0) rows.push(current);
  return rows;
}



/**
 * Computes gutter numbers for wrapped-mode display.
 * The line number appears on the topmost visible visual row of each logical line.
 * Subsequent visual rows of the same line get null (empty gutter cell).
 *
 * Algorithm:
 * 1. Split content into visual rows (reuses splitIntoVisualRows).
 * 2. Walk the raw content character-by-character, tracking which visual row
 *    index we're on and which logical line we're in.
 * 3. For each visual row:
 *    - If it's the first visual row of a new logical line → line number.
 *    - If characterOffset > 0 and this is the first row (topmost visible row
 *      of a partially-scrolled line) → line number (Req 3.2).
 *    - Otherwise → null.
 *
 * @param content Response content string (with delimiters)
 * @param colCount Characters per visual row
 * @param startLine Zero-based starting logical line
 * @param characterOffset Character offset within startLine (0 = first row visible)
 * @returns Array of (1-based line number | null) per visual row
 */
export function computeWrappedGutterNumbers(
  content: string,
  colCount: number,
  startLine: number,
  characterOffset: number
): (number | null)[] {
  const rows = splitIntoVisualRows(content, colCount);
  if (rows.length === 0) return [];

  const gutterNumbers: (number | null)[] = [];
  let currentLine = startLine;

  // First row: always gets line number (topmost-visible-row rule, Req 3.1/3.2/3.3)
  gutterNumbers.push(currentLine + 1);

  // Walk content to detect line boundaries and assign gutter numbers to remaining rows
  let rowIdx = 1;
  let colPos = 0;
  for (let i = 0; i < content.length && rowIdx < rows.length; i++) {
    const ch = content[i];
    if (ch === '\n' || ch === '\r') {
      // Handle \r\n as single delimiter
      if (ch === '\r' && i + 1 < content.length && content[i + 1] === '\n') i++;
      // New logical line starts on next visual row
      currentLine++;
      colPos = 0;
      if (rowIdx < rows.length) {
        gutterNumbers.push(currentLine + 1);
        rowIdx++;
      }
    } else {
      colPos++;
      if (colPos === colCount) {
        colPos = 0;
        // Wrapped continuation of same logical line — no line number
        if (rowIdx < rows.length) {
          gutterNumbers.push(null);
          rowIdx++;
        }
      }
    }
  }

  return gutterNumbers;
}
