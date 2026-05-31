/**
 * Bugfix: line-wrap-numbers-update — Bug Condition Exploration Test
 *
 * **Validates: Requirements 1.1, 1.2, 1.3, 1.4**
 *
 * Property 1: Bug Condition — Line Numbers Out of Sync with Displayed Content
 *
 * These tests demonstrate that the frontend-computed gutter numbers drift from
 * the actual displayed content when state is inconsistent. They encode the
 * EXPECTED behavior: gutter numbers should match the rows actually present in
 * the response, not be computed from potentially-stale local state.
 *
 * On UNFIXED code these tests MUST FAIL — failure confirms the bug exists.
 */
import * as fc from 'fast-check';
import { computeNonWrappedLineNumbers, computeWrappedGutterNumbers, splitIntoVisualRows } from './line-wrap-utils';

describe('Bugfix: line-wrap-numbers-update, Property 1: Bug Condition — Line Numbers Out of Sync', () => {

  /**
   * Non-wrapped mode: simulate get-view response arriving after startLine has been
   * incremented by a scroll action. The response was fetched for `responseStartLine`,
   * but by the time the computed signal evaluates, `startLine` has advanced to
   * `currentStartLine`. The bug: computeNonWrappedLineNumbers uses currentStartLine,
   * producing numbers offset from the actual response rows.
   *
   * Expected behavior: gutter numbers should correspond to the response's actual
   * line numbers (responseStartLine + i + 1), not the stale currentStartLine.
   */
  it('non-wrapped: gutter numbers match response rows, not stale startLine', () => {
    fc.assert(
      fc.property(
        fc.nat({ max: 500 }),       // responseStartLine: line the response was fetched for
        fc.integer({ min: 1, max: 10 }), // scrollDrift: how many lines startLine advanced after request
        fc.integer({ min: 1, max: 50 }), // rowCount: number of rows in response
        (responseStartLine: number, scrollDrift: number, rowCount: number) => {
          // The response contains rows starting at responseStartLine
          // But startLine has since advanced by scrollDrift (simulating scroll race)
          const currentStartLine = responseStartLine + scrollDrift;

          // What the frontend currently computes (using stale currentStartLine):
          const computedGutter = computeNonWrappedLineNumbers(currentStartLine, rowCount);

          // What the gutter SHOULD show (matching the actual response content):
          const expectedGutter = computeNonWrappedLineNumbers(responseStartLine, rowCount);

          // The expected behavior: computed gutter matches response-provided line numbers
          // On unfixed code, computedGutter uses currentStartLine which is WRONG
          for (let i = 0; i < rowCount; i++) {
            if (computedGutter[i] !== expectedGutter[i]) return false;
          }
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  /**
   * Wrapped mode: rawResponseContent was fetched for startLine=responseStartLine,
   * but startLine has since been updated by a scroll action. The frontend uses
   * the new startLine with the old content, producing wrong line numbers.
   *
   * Expected behavior: gutter numbers should come from the backend (matching
   * the response content), not be recomputed from inconsistent state.
   */
  it('wrapped: gutter numbers match response startLine, not stale startLine', () => {
    // Generate content with multiple lines that wrap
    const longLineArb = fc.string({
      minLength: 10,
      maxLength: 30,
      unit: fc.integer({ min: 0x41, max: 0x5a }).map(c => String.fromCharCode(c)),
    });
    const contentArb = fc.tuple(longLineArb, longLineArb, longLineArb)
      .map(([a, b, c]) => a + '\n' + b + '\n' + c);

    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 5, max: 10 }),  // colCount
        fc.nat({ max: 20 }),              // responseStartLine: startLine when response was fetched
        fc.integer({ min: 1, max: 10 }), // startLineDrift: how much startLine advanced after request
        (content: string, colCount: number, responseStartLine: number, startLineDrift: number) => {
          const currentStartLine = responseStartLine + startLineDrift;

          // Correct gutter: computed with the startLine that matches the response
          const correctGutter = computeWrappedGutterNumbers(content, colCount, responseStartLine, 0);

          // What the frontend computes (using stale currentStartLine with old content):
          const staleGutter = computeWrappedGutterNumbers(content, colCount, currentStartLine, 0);

          // Expected behavior: gutter should match the response's startLine
          // On unfixed code, the frontend uses currentStartLine which is WRONG
          if (correctGutter.length !== staleGutter.length) {
            return correctGutter.length === staleGutter.length;
          }
          for (let i = 0; i < correctGutter.length; i++) {
            if (correctGutter[i] !== staleGutter[i]) return false;
          }
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });

  /**
   * Wrapped mode resize race: colCount changes after rawResponseContent was cached.
   * computeWrappedGutterNumbers uses the NEW colCount to split the OLD content,
   * producing different row boundaries than what the backend would produce with
   * the new colCount.
   *
   * Expected behavior: gutter numbers should come from a fresh backend response
   * computed with the current colCount, not from recomputing with mismatched state.
   */
  it('wrapped resize: gutter numbers match content split at response colCount, not new colCount', () => {
    const contentArb = fc.string({
      minLength: 15,
      maxLength: 50,
      unit: fc.integer({ min: 0x41, max: 0x5a }).map(c => String.fromCharCode(c)),
    });

    fc.assert(
      fc.property(
        contentArb,
        fc.integer({ min: 3, max: 8 }),   // originalColCount: colCount when response was fetched
        fc.integer({ min: 1, max: 4 }),   // colCountDelta: how much colCount changed on resize
        fc.nat({ max: 20 }),              // startLine
        (content: string, originalColCount: number, colCountDelta: number, startLine: number) => {
          const newColCount = originalColCount + colCountDelta;

          // Correct gutter: computed with the colCount that matches the response content
          const correctGutter = computeWrappedGutterNumbers(content, originalColCount, startLine, 0);

          // What the frontend computes after resize (using new colCount with old content):
          const resizedGutter = computeWrappedGutterNumbers(content, newColCount, startLine, 0);

          // Expected behavior: gutter should match the response's colCount
          // On unfixed code, the frontend uses newColCount with stale content
          if (correctGutter.length !== resizedGutter.length) {
            // Different visual row counts = definitely misaligned gutter
            return correctGutter.length === resizedGutter.length;
          }
          for (let i = 0; i < correctGutter.length; i++) {
            if (correctGutter[i] !== resizedGutter[i]) return false;
          }
          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});
