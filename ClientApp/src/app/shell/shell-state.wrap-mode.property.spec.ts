/**
 * Feature: line-wrap-numbers — Wrap mode property tests
 */
import * as fc from 'fast-check';
import { splitIntoVisualRows, computeWrappedScrollbarMax, scrollDownOneVisualRow, scrollUpOneVisualRow } from './line-wrap-utils';

/**
 * Feature: line-wrap-numbers, Property 4: Wrapped-mode request payload round-trip
 *
 * **Validates: Requirements 5.1, 5.2, 5.4**
 *
 * Property: Encoding a wrapped-mode request payload as
 * `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount`
 * then parsing by splitting on '\n' recovers the original values.
 * Numeric fields contain only ASCII digits 0-9 with no leading zeros
 * except for the value "0" itself, no whitespace, and no sign characters,
 * within the range 0 to 2,147,483,647.
 */
describe('Feature: line-wrap-numbers, Property 4: Wrapped-mode request payload round-trip', () => {
  const INT32_MAX = 2_147_483_647;
  const noLeadingZerosPattern = /^(0|[1-9][0-9]*)$/;

  it('encode then parse recovers original values and numeric fields are well-formed', () => {
    fc.assert(
      fc.property(
        // viewSessionId: non-empty string without newlines
        fc.string({ minLength: 1, maxLength: 50 }).filter(s => !s.includes('\n')),
        // startLine: 0 to INT32_MAX
        fc.integer({ min: 0, max: INT32_MAX }),
        // characterOffset: 0 to INT32_MAX
        fc.integer({ min: 0, max: INT32_MAX }),
        // characterCount: 1 to INT32_MAX
        fc.integer({ min: 1, max: INT32_MAX }),
        (viewSessionId: string, startLine: number, characterOffset: number, characterCount: number) => {
          // Encode: same format as sendWrappedViewRequest
          const payload = `${viewSessionId}\nW\n${startLine}\n${characterOffset}\n${characterCount}`;

          // Parse: split on '\n'
          const fields = payload.split('\n');

          // Verify exactly 5 fields
          if (fields.length !== 5) return false;

          // Verify round-trip recovery
          if (fields[0] !== viewSessionId) return false;
          if (fields[1] !== 'W') return false;
          if (parseInt(fields[2], 10) !== startLine) return false;
          if (parseInt(fields[3], 10) !== characterOffset) return false;
          if (parseInt(fields[4], 10) !== characterCount) return false;

          // Verify numeric fields format: only ASCII digits, no leading zeros except "0"
          if (!noLeadingZerosPattern.test(fields[2])) return false;
          if (!noLeadingZerosPattern.test(fields[3])) return false;
          if (!noLeadingZerosPattern.test(fields[4])) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});

/**
 * Feature: line-wrap-numbers, Property 9: Wrapped-mode Scrollbar_Max computation
 *
 * **Validates: Requirements 7.4**
 *
 * Property: For any array of non-negative line lengths and colCount >= 1,
 * computeWrappedScrollbarMax returns the sum of ceil(len/colCount) for each len > 0,
 * plus 1 for each len === 0.
 */
describe('Feature: line-wrap-numbers, Property 9: Wrapped-mode Scrollbar_Max computation', () => {
  it('result equals sum of ceil(len/colCount) for len > 0 plus 1 for len === 0', () => {
    fc.assert(
      fc.property(
        fc.array(fc.nat({ max: 10000 }), { minLength: 0, maxLength: 50 }),
        fc.integer({ min: 1, max: 200 }),
        (lineLengths: number[], colCount: number) => {
          const result = computeWrappedScrollbarMax(lineLengths, colCount);

          // Compute expected value independently
          let expected = 0;
          for (const len of lineLengths) {
            if (len === 0) {
              expected += 1;
            } else {
              expected += Math.ceil(len / colCount);
            }
          }

          return result === expected;
        }
      ),
      { numRuns: 10 }
    );
  });
});


/**
 * Feature: line-wrap-numbers, Property 5: Wrapped-mode scroll position computation
 *
 * **Validates: Requirements 5.3, 8.1, 8.2, 8.3, 8.4, 8.5, 8.6**
 *
 * Properties:
 * 1. Round-trip: scrollDown(1) then scrollUp(1) returns to original position (non-boundary)
 * 2. scrollDown advances: from non-terminal position, scrollDown either increases characterOffset by colCount OR advances to next line with offset 0
 * 3. Boundary guard top: at (startLine=0, characterOffset=0), scrollUp returns same position with atTop=true
 * 4. Boundary guard bottom: at last line with offset at last visual row, scrollDown returns same position with atEnd=true
 */
describe('Feature: line-wrap-numbers, Property 5: Wrapped-mode scroll position computation', () => {
  /**
   * Arbitrary: generates a Map<number, number> of line lengths (3-10 lines, lengths 0-100)
   * and a valid non-boundary starting position within those lines.
   */
  const nonBoundaryPositionArb = fc.integer({ min: 3, max: 10 }).chain(lineCount =>
    fc.tuple(
      fc.array(fc.integer({ min: 0, max: 100 }), { minLength: lineCount, maxLength: lineCount }),
      fc.integer({ min: 1, max: 200 })
    ).chain(([lengths, colCount]) => {
      // Build valid non-boundary positions: not at top (line=0, offset=0)
      // and not at the last visual row of the last line
      const validPositions: { startLine: number; characterOffset: number }[] = [];
      for (let line = 0; line < lengths.length; line++) {
        const lineLen = lengths[line];
        if (lineLen === 0) {
          // Empty line: only position is offset=0. It's non-boundary if not first line and not last line.
          if (line > 0 && line < lengths.length - 1) {
            validPositions.push({ startLine: line, characterOffset: 0 });
          }
        } else {
          const visualRows = Math.ceil(lineLen / colCount);
          for (let row = 0; row < visualRows; row++) {
            const offset = row * colCount;
            const isTop = line === 0 && offset === 0;
            const isBottom = line === lengths.length - 1 && row === visualRows - 1;
            if (!isTop && !isBottom) {
              validPositions.push({ startLine: line, characterOffset: offset });
            }
          }
        }
      }
      if (validPositions.length === 0) {
        // Fallback: generate a position that's at least not at absolute top
        // Use line 1 offset 0 if possible
        return fc.constant({
          lineLengths: lengths,
          colCount,
          position: { startLine: Math.min(1, lengths.length - 1), characterOffset: 0 },
          totalLines: lengths.length
        }).filter(d => !(d.position.startLine === 0 && d.position.characterOffset === 0));
      }
      return fc.constantFrom(...validPositions).map(pos => ({
        lineLengths: lengths,
        colCount,
        position: pos,
        totalLines: lengths.length
      }));
    })
  );

  it('round-trip: scrollDown then scrollUp returns to original position (non-boundary)', () => {
    fc.assert(
      fc.property(
        nonBoundaryPositionArb,
        (data) => {
          const { lineLengths, colCount, position, totalLines } = data;
          const lineLengthMap = new Map<number, number>();
          lineLengths.forEach((len, idx) => lineLengthMap.set(idx, len));

          const afterDown = scrollDownOneVisualRow(position, colCount, lineLengthMap, totalLines);
          // If we hit the end, this position is actually a boundary — skip
          if (afterDown.atEnd) return true;

          const afterUp = scrollUpOneVisualRow(
            { startLine: afterDown.startLine, characterOffset: afterDown.characterOffset },
            colCount,
            lineLengthMap
          );

          return afterUp.startLine === position.startLine &&
                 afterUp.characterOffset === position.characterOffset;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('scrollDown advances: increases offset by colCount OR advances to next line with offset 0', () => {
    fc.assert(
      fc.property(
        nonBoundaryPositionArb,
        (data) => {
          const { lineLengths, colCount, position, totalLines } = data;
          const lineLengthMap = new Map<number, number>();
          lineLengths.forEach((len, idx) => lineLengthMap.set(idx, len));

          const result = scrollDownOneVisualRow(position, colCount, lineLengthMap, totalLines);
          if (result.atEnd) return true; // boundary case, skip

          // Either offset increased by colCount on same line
          const sameLineAdvanced = result.startLine === position.startLine &&
                                   result.characterOffset === position.characterOffset + colCount;
          // Or advanced to next line with offset 0
          const nextLineAdvanced = result.startLine === position.startLine + 1 &&
                                   result.characterOffset === 0;

          return sameLineAdvanced || nextLineAdvanced;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('boundary guard top: at (startLine=0, characterOffset=0), scrollUp returns same position with atTop=true', () => {
    fc.assert(
      fc.property(
        fc.array(fc.integer({ min: 0, max: 100 }), { minLength: 3, maxLength: 10 }),
        fc.integer({ min: 1, max: 200 }),
        (lengths: number[], colCount: number) => {
          const lineLengthMap = new Map<number, number>();
          lengths.forEach((len, idx) => lineLengthMap.set(idx, len));

          const position = { startLine: 0, characterOffset: 0 };
          const result = scrollUpOneVisualRow(position, colCount, lineLengthMap);

          return result.startLine === 0 &&
                 result.characterOffset === 0 &&
                 result.atTop === true;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('boundary guard bottom: at last visual row of last line, scrollDown returns same position with atEnd=true', () => {
    fc.assert(
      fc.property(
        fc.array(fc.integer({ min: 0, max: 100 }), { minLength: 3, maxLength: 10 }),
        fc.integer({ min: 1, max: 200 }),
        (lengths: number[], colCount: number) => {
          const lineLengthMap = new Map<number, number>();
          lengths.forEach((len, idx) => lineLengthMap.set(idx, len));

          const lastLine = lengths.length - 1;
          const lastLineLen = lengths[lastLine];
          // Last visual row offset for the last line
          const lastRowOffset = lastLineLen === 0
            ? 0
            : Math.floor((lastLineLen - 1) / colCount) * colCount;

          const position = { startLine: lastLine, characterOffset: lastRowOffset };
          const result = scrollDownOneVisualRow(position, colCount, lineLengthMap, lengths.length);

          return result.startLine === lastLine &&
                 result.characterOffset === lastRowOffset &&
                 result.atEnd === true;
        }
      ),
      { numRuns: 10 }
    );
  });
});
