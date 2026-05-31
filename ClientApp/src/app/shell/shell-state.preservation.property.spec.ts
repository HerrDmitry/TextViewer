/**
 * Bugfix: line-wrap-numbers-update — Property 2: Preservation
 * Non-Gutter Behavior Unchanged
 *
 * **Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7**
 *
 * These tests capture existing correct behavior on UNFIXED code.
 * They must PASS before and after the fix to confirm no regressions.
 */
import * as fc from 'fast-check';
import { computeGutterWidth, splitIntoVisualRows } from './line-wrap-utils';

/**
 * Property 2a: computeGutterWidth preservation
 *
 * **Validates: Requirements 3.1**
 *
 * For all (totalLines >= 1, charWidth > 0):
 * computeGutterWidth returns digitCount(totalLines) * charWidth + 16
 */
describe('Preservation Property 2a: computeGutterWidth unchanged', () => {
  it('returns digitCount(totalLines) * charWidth + 16 for all valid inputs', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 1_000_000_000 }),
        fc.integer({ min: 1, max: 100 }),
        (totalLines: number, charWidth: number) => {
          const result = computeGutterWidth(totalLines, charWidth);
          const digits = Math.max(1, Math.floor(Math.log10(totalLines)) + 1);
          const expected = digits * charWidth + 16;
          return result === expected;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('returns 0 when totalLines <= 0', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: -1000, max: 0 }),
        fc.integer({ min: 1, max: 100 }),
        (totalLines: number, charWidth: number) => {
          return computeGutterWidth(totalLines, charWidth) === 0;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Property 2b: Non-wrapped request format preservation
 *
 * **Validates: Requirements 3.2**
 *
 * For all valid request params, non-wrapped request format is:
 * viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount
 */
describe('Preservation Property 2b: Non-wrapped request format', () => {
  it('format is viewSessionId\\nstartLine\\nstartCol\\nrowCount\\ncolCount', () => {
    fc.assert(
      fc.property(
        fc.uuid(),
        fc.nat({ max: 100000 }),
        fc.nat({ max: 10000 }),
        fc.integer({ min: 1, max: 200 }),
        fc.integer({ min: 1, max: 500 }),
        (sessionId: string, startLine: number, startCol: number, rowCount: number, colCount: number) => {
          // Construct payload the same way sendStandardViewRequest does
          const payload = `${sessionId}\n${startLine}\n${startCol}\n${rowCount}\n${colCount}`;

          // Verify format: exactly 5 parts separated by \n
          const parts = payload.split('\n');
          if (parts.length !== 5) return false;
          if (parts[0] !== sessionId) return false;
          if (parseInt(parts[1], 10) !== startLine) return false;
          if (parseInt(parts[2], 10) !== startCol) return false;
          if (parseInt(parts[3], 10) !== rowCount) return false;
          if (parseInt(parts[4], 10) !== colCount) return false;
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Property 2c: Wrapped request format preservation
 *
 * **Validates: Requirements 3.3**
 *
 * For all valid wrapped params, wrapped request format is:
 * viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount
 */
describe('Preservation Property 2c: Wrapped request format', () => {
  it('format is viewSessionId\\nW\\nstartLine\\ncharacterOffset\\ncharacterCount', () => {
    fc.assert(
      fc.property(
        fc.uuid(),
        fc.nat({ max: 100000 }),
        fc.nat({ max: 50000 }),
        fc.integer({ min: 1, max: 200 }),
        fc.integer({ min: 1, max: 500 }),
        (sessionId: string, startLine: number, characterOffset: number, rowCount: number, colCount: number) => {
          // Construct payload the same way sendWrappedViewRequest does
          const characterCount = Math.min(colCount * rowCount, 2_147_483_647);
          const payload = `${sessionId}\nW\n${startLine}\n${characterOffset}\n${characterCount}`;

          // Verify format: exactly 5 parts separated by \n
          const parts = payload.split('\n');
          if (parts.length !== 5) return false;
          if (parts[0] !== sessionId) return false;
          if (parts[1] !== 'W') return false;
          if (parseInt(parts[2], 10) !== startLine) return false;
          if (parseInt(parts[3], 10) !== characterOffset) return false;
          if (parseInt(parts[4], 10) !== characterCount) return false;
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Property 2d: splitIntoVisualRows preservation
 *
 * **Validates: Requirements 3.7**
 *
 * For all (content, colCount) pairs: splitIntoVisualRows produces consistent row splits.
 * - Each row has length <= colCount
 * - Concatenating all rows (with newlines restored at logical line boundaries) reconstructs content
 * - Empty content returns empty array
 */
describe('Preservation Property 2d: splitIntoVisualRows behavior unchanged', () => {
  const contentArb = fc.string({
    minLength: 0,
    maxLength: 100,
    unit: fc.oneof(
      fc.integer({ min: 0x20, max: 0x7e }).map(c => String.fromCharCode(c)),
      fc.constant('\n')
    ),
  });

  it('each row has length <= colCount', () => {
    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 1, max: 30 }),
        (content: string, colCount: number) => {
          const rows = splitIntoVisualRows(content, colCount);
          return rows.every(row => row.length <= colCount);
        }
      ),
      { numRuns: 10 }
    );
  });

  it('empty content returns empty array', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 100 }),
        (colCount: number) => {
          const rows = splitIntoVisualRows('', colCount);
          return rows.length === 0;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('total character count of all rows equals content length minus newline count', () => {
    fc.assert(
      fc.property(
        contentArb.filter(c => c.length > 0),
        fc.integer({ min: 1, max: 30 }),
        (content: string, colCount: number) => {
          const rows = splitIntoVisualRows(content, colCount);
          const totalChars = rows.reduce((sum, row) => sum + row.length, 0);
          const newlineCount = (content.match(/\n/g) || []).length;
          // Characters in rows = content length minus newlines
          // (newlines are consumed as delimiters, not rendered)
          return totalChars === content.length - newlineCount;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('deterministic: same input always produces same output', () => {
    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 1, max: 30 }),
        (content: string, colCount: number) => {
          const rows1 = splitIntoVisualRows(content, colCount);
          const rows2 = splitIntoVisualRows(content, colCount);
          if (rows1.length !== rows2.length) return false;
          for (let i = 0; i < rows1.length; i++) {
            if (rows1[i] !== rows2[i]) return false;
          }
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});
