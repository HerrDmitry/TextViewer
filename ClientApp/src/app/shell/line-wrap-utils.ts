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
 * Computes the total number of visual rows for wrapped-mode vertical scrollbar max.
 * For each logical line: if content length is 0, contributes 1 visual row (empty line);
 * otherwise contributes ceil(length / colCount) visual rows.
 *
 * @param lineLengths Array of content lengths (excluding delimiters) for each logical line
 * @param colCount Number of characters per visual row
 * @returns Total visual row count (Scrollbar_Max in wrapped mode)
 */
export function computeWrappedScrollbarMax(lineLengths: number[], colCount: number): number {
  let total = 0;
  for (const len of lineLengths) {
    if (len === 0) {
      total += 1; // empty line = 1 visual row
    } else {
      total += Math.ceil(len / colCount);
    }
  }
  return total;
}

/**
 * Scroll down by one visual row in wrapped mode.
 * Increases characterOffset by colCount. If offset >= line content length,
 * advances to next line with offset 0.
 *
 * Boundary guard (Req 8.5): If startLine is the last line AND
 * characterOffset + colCount >= lineLen, this is the last visual row →
 * returns the same position unchanged (no request sent).
 *
 * @param state Current scroll position (startLine, characterOffset)
 * @param colCount Characters per visual row
 * @param lineLengths Map of line index → content length (excluding delimiter)
 * @param totalLogicalLines Total number of logical lines in the file
 * @returns New position and whether the end of file was reached
 */
export function scrollDownOneVisualRow(
  state: { startLine: number; characterOffset: number },
  colCount: number,
  lineLengths: Map<number, number>,
  totalLogicalLines: number
): { startLine: number; characterOffset: number; atEnd: boolean } {
  const { startLine, characterOffset } = state;
  const lineLen = lineLengths.get(startLine) ?? 0;

  if (lineLen === 0) {
    // Empty line — advance to next line if not last
    if (startLine + 1 >= totalLogicalLines) {
      return { startLine, characterOffset, atEnd: true };
    }
    return { startLine: startLine + 1, characterOffset: 0, atEnd: false };
  }

  const newOffset = characterOffset + colCount;
  if (newOffset >= lineLen) {
    // Crossed line boundary → next line, offset 0
    if (startLine + 1 >= totalLogicalLines) {
      return { startLine, characterOffset, atEnd: true };
    }
    return { startLine: startLine + 1, characterOffset: 0, atEnd: false };
  }
  return { startLine, characterOffset: newOffset, atEnd: false };
}

/**
 * Scroll up by one visual row in wrapped mode.
 * Decreases characterOffset by colCount. If negative,
 * moves to previous line's last wrapped row.
 *
 * Boundary guard (Req 8.4): If startLine == 0 AND characterOffset == 0,
 * returns same position unchanged (no request sent).
 *
 * @param state Current scroll position (startLine, characterOffset)
 * @param colCount Characters per visual row
 * @param lineLengths Map of line index → content length (excluding delimiter)
 * @returns New position and whether the top of file was reached
 */
export function scrollUpOneVisualRow(
  state: { startLine: number; characterOffset: number },
  colCount: number,
  lineLengths: Map<number, number>
): { startLine: number; characterOffset: number; atTop: boolean } {
  const { startLine, characterOffset } = state;

  // Already at top
  if (startLine === 0 && characterOffset === 0) {
    return { startLine: 0, characterOffset: 0, atTop: true };
  }

  const newOffset = characterOffset - colCount;
  if (newOffset >= 0) {
    return { startLine, characterOffset: newOffset, atTop: false };
  }

  // Move to previous line
  if (startLine === 0) {
    return { startLine: 0, characterOffset: 0, atTop: true };
  }

  const prevLine = startLine - 1;
  const prevLineLen = lineLengths.get(prevLine) ?? 0;

  if (prevLineLen === 0) {
    return { startLine: prevLine, characterOffset: 0, atTop: false };
  }

  // Last wrapped row of previous line
  const lastRowOffset = Math.floor((prevLineLen - 1) / colCount) * colCount;
  return { startLine: prevLine, characterOffset: lastRowOffset, atTop: false };
}

/**
 * Applies N visual-row scroll steps iteratively (Req 8.6).
 * Each step applies scrollDownOneVisualRow/scrollUpOneVisualRow independently,
 * which correctly handles boundary crossing over short/empty lines.
 * Stops early if boundary reached (atEnd/atTop).
 * Returns final position and whether a request should be sent.
 *
 * @param state Current scroll position (startLine, characterOffset)
 * @param steps Number of visual rows to scroll (positive = down, negative = up)
 * @param colCount Characters per visual row
 * @param lineLengths Map of line index → content length (excluding delimiter)
 * @param totalLogicalLines Total number of logical lines in the file
 * @returns Final position and whether position changed from original
 */
export function scrollByVisualRows(
  state: { startLine: number; characterOffset: number },
  steps: number,
  colCount: number,
  lineLengths: Map<number, number>,
  totalLogicalLines: number
): { startLine: number; characterOffset: number; positionChanged: boolean } {
  let current = { startLine: state.startLine, characterOffset: state.characterOffset };
  const originalStart = state.startLine;
  const originalOffset = state.characterOffset;
  const absSteps = Math.abs(steps);

  for (let i = 0; i < absSteps; i++) {
    if (steps > 0) {
      const result = scrollDownOneVisualRow(current, colCount, lineLengths, totalLogicalLines);
      if (result.atEnd) break;
      current = { startLine: result.startLine, characterOffset: result.characterOffset };
    } else {
      const result = scrollUpOneVisualRow(current, colCount, lineLengths);
      if (result.atTop) break;
      current = { startLine: result.startLine, characterOffset: result.characterOffset };
    }
  }

  const positionChanged = current.startLine !== originalStart || current.characterOffset !== originalOffset;
  return { ...current, positionChanged };
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
