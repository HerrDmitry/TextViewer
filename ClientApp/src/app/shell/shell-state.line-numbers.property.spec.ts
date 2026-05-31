/**
 * Feature: line-wrap-numbers — Line number property tests
 */
import * as fc from 'fast-check';
import { computeGutterWidth, computeNonWrappedLineNumbers, computeColCount, computeWrappedGutterNumbers, splitIntoVisualRows } from './line-wrap-utils';

/**
 * Feature: line-wrap-numbers, Property 1: Non-wrapped line number computation
 *
 * **Validates: Requirements 1.1, 1.5, 1.7, 2.1**
 *
 * Property: For any startLine >= 0 and rowCount >= 0,
 * computeNonWrappedLineNumbers returns an array of length rowCount
 * where each element at index i equals startLine + i + 1.
 */
describe('Feature: line-wrap-numbers, Property 1: Non-wrapped line number computation', () => {
  it('array length equals rowCount and each element at index i equals startLine + i + 1', () => {
    fc.assert(
      fc.property(
        fc.nat({ max: 100000 }),  // startLine >= 0
        fc.nat({ max: 200 }),     // rowCount >= 0
        (startLine: number, rowCount: number) => {
          const result = computeNonWrappedLineNumbers(startLine, rowCount);

          // Array length must equal rowCount
          if (result.length !== rowCount) return false;

          // Each element at index i must equal startLine + i + 1
          for (let i = 0; i < rowCount; i++) {
            if (result[i] !== startLine + i + 1) return false;
          }

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Feature: line-wrap-numbers, Property 2: Gutter width computation
 *
 * **Validates: Requirements 1.4**
 *
 * Property: For any totalLogicalLines ≥ 1 and charWidth > 0,
 * computeGutterWidth returns max(1, floor(log10(totalLines)) + 1) * charWidth + 16
 */
describe('Feature: line-wrap-numbers, Property 2: Gutter width computation', () => {
  it('result equals max(1, floor(log10(totalLines)) + 1) * charWidth + 16 for any totalLogicalLines >= 1 and charWidth > 0', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: 1, max: 1_000_000_000 }),
        fc.float({ min: Math.fround(0.1), max: Math.fround(100), noNaN: true, noDefaultInfinity: true }),
        (totalLogicalLines: number, charWidth: number) => {
          const result = computeGutterWidth(totalLogicalLines, charWidth);
          const expectedDigits = Math.max(1, Math.floor(Math.log10(totalLogicalLines)) + 1);
          const expected = expectedDigits * charWidth + 16;
          return Math.abs(result - expected) < 1e-10;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Feature: line-wrap-numbers, Property 10: Col_Count computation with gutter
 *
 * **Validates: Requirements 9.1, 9.3, 9.4**
 *
 * Property: For any availablePixelWidth, gutterWidth, and charWidth > 0,
 * computeColCount returns max(1, floor((availablePixelWidth - gutterWidth) / charWidth)).
 */
describe('Feature: line-wrap-numbers, Property 10: Col_Count computation with gutter', () => {
  it('result equals max(1, floor((pixelWidth - gutterWidth) / charWidth))', () => {
    fc.assert(
      fc.property(
        fc.float({ min: 0, max: 5000, noNaN: true }),
        fc.float({ min: 0, max: 2000, noNaN: true }),
        fc.float({ min: Math.fround(0.1), max: 100, noNaN: true }),
        (availablePixelWidth: number, gutterWidth: number, charWidth: number) => {
          const result = computeColCount(availablePixelWidth, gutterWidth, charWidth);
          const expected = Math.max(1, Math.floor((availablePixelWidth - gutterWidth) / charWidth));
          return result === expected;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Feature: line-wrap-numbers, Property 3: Wrapped-mode gutter number placement
 *
 * **Validates: Requirements 3.1, 3.2, 3.3**
 *
 * Property: For any content with newlines and a valid colCount,
 * computeWrappedGutterNumbers produces exactly one non-null entry per logical line visible,
 * placed on the topmost visible row. Non-null entries are in ascending order.
 */
describe('Feature: line-wrap-numbers, Property 3: Wrapped-mode gutter number placement', () => {
  /**
   * Generator for content strings: printable ASCII chars mixed with newlines.
   * Uses fc.string with a unit that produces printable chars or newlines.
   */
  const contentArb = fc.string({
    minLength: 1,
    maxLength: 80,
    unit: fc.oneof(
      fc.integer({ min: 0x20, max: 0x7e }).map(c => String.fromCharCode(c)),
      fc.constant('\n')
    ),
  });

  it('result array has the same length as splitIntoVisualRows(content, colCount)', () => {
    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 1, max: 20 }),
        fc.nat({ max: 50 }),
        (content: string, colCount: number, startLine: number) => {
          const result = computeWrappedGutterNumbers(content, colCount, startLine, 0);
          const visualRows = splitIntoVisualRows(content, colCount);
          return result.length === visualRows.length;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('each non-null entry appears exactly once per logical line (no duplicate line numbers)', () => {
    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 1, max: 20 }),
        fc.nat({ max: 50 }),
        (content: string, colCount: number, startLine: number) => {
          const result = computeWrappedGutterNumbers(content, colCount, startLine, 0);
          const nonNulls = result.filter((v): v is number => v !== null);
          // No duplicate line numbers
          const unique = new Set(nonNulls);
          return unique.size === nonNulls.length;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('the first row always gets a line number (topmost-visible-row rule)', () => {
    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 1, max: 20 }),
        fc.nat({ max: 50 }),
        (content: string, colCount: number, startLine: number) => {
          const result = computeWrappedGutterNumbers(content, colCount, startLine, 0);
          if (result.length === 0) return true; // empty content edge case
          return result[0] !== null;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('non-null entries are in ascending order (line numbers increase)', () => {
    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 1, max: 20 }),
        fc.nat({ max: 50 }),
        (content: string, colCount: number, startLine: number) => {
          const result = computeWrappedGutterNumbers(content, colCount, startLine, 0);
          const nonNulls = result.filter((v): v is number => v !== null);
          for (let i = 1; i < nonNulls.length; i++) {
            if (nonNulls[i] <= nonNulls[i - 1]) return false;
          }
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('for content with N newlines, there should be N+1 distinct line numbers (one per logical line visible)', () => {
    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 1, max: 20 }),
        fc.nat({ max: 50 }),
        (content: string, colCount: number, startLine: number) => {
          const result = computeWrappedGutterNumbers(content, colCount, startLine, 0);
          const nonNulls = result.filter((v): v is number => v !== null);
          // The number of distinct line numbers equals the number of logical lines
          // that produce at least one visual row in splitIntoVisualRows.
          // A trailing newline creates a logical line boundary but the subsequent
          // empty line only gets a visual row if there's content after it.
          // Count logical lines that produce visual rows by walking the content:
          const visualRows = splitIntoVisualRows(content, colCount);
          // nonNulls count should equal the number of visual rows that are the
          // topmost row of their logical line — which is the same as the number
          // of distinct logical lines represented in the visual rows.
          // Since each logical line gets exactly one non-null entry (tested above),
          // and non-null entries are unique (tested above), we verify that the
          // count matches the number of logical lines that have visual rows.
          // A logical line has visual rows iff it contributes at least one row to splitIntoVisualRows.
          // For content that doesn't end with newline: N newlines → N+1 logical lines all visible.
          // For content that ends with newline: the last empty logical line has no visual row.
          const endsWithNewline = content.endsWith('\n');
          const newlineCount = (content.match(/\n/g) || []).length;
          const expectedLogicalLines = endsWithNewline ? newlineCount : newlineCount + 1;
          return nonNulls.length === expectedLogicalLines;
        }
      ),
      { numRuns: 10 }
    );
  });
});
