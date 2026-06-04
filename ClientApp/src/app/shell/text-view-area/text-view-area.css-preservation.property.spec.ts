/**
 * Bugfix: short-line-horizontal-scroll — Preservation Property Test
 *
 * **Validates: Requirements 3.1, 3.2, 3.3, 3.4**
 *
 * Property 2: Preservation — Non-Empty Rows Unchanged
 *
 * This test verifies that for all viewRows arrays where every row is non-empty,
 * all `.view-row` elements have equal `offsetHeight` and that height equals the
 * height of any single non-empty row.
 *
 * On UNFIXED code this test MUST PASS — non-empty rows already render correctly.
 */
import * as fc from 'fast-check';

describe('Bugfix: short-line-horizontal-scroll, Property 2: Preservation — Non-Empty Rows Unchanged', () => {

  let container: HTMLDivElement;

  beforeEach(() => {
    // Inject the current .view-row CSS into JSDOM
    const style = document.createElement('style');
    style.textContent = `
      .view-row {
        font-family: monospace;
        font-size: 14px;
        white-space: pre;
        line-height: normal;
        min-height: 1lh;
      }
    `;
    document.head.appendChild(style);

    container = document.createElement('div');
    document.body.appendChild(container);
  });

  afterEach(() => {
    document.body.removeChild(container);
    // Remove injected styles
    const styles = document.head.querySelectorAll('style');
    styles.forEach(s => s.remove());
  });

  /**
   * Property: for all viewRows arrays where every row is non-empty,
   * all `.view-row` elements have equal offsetHeight and that height
   * equals the height of any single non-empty row.
   */
  it('non-empty rows should all have equal offsetHeight', () => {
    const nonEmptyRowsArb = fc.array(
      fc.string({ minLength: 1, maxLength: 40 }),
      { minLength: 1, maxLength: 10 }
    );

    fc.assert(
      fc.property(
        nonEmptyRowsArb,
        (viewRows: string[]) => {
          // Clear container
          container.innerHTML = '';

          // Create DOM elements matching template pattern
          for (const row of viewRows) {
            const div = document.createElement('div');
            div.className = 'view-row';
            div.textContent = row;
            container.appendChild(div);
          }

          const elements = container.querySelectorAll('.view-row');
          const heights: number[] = [];
          for (let i = 0; i < elements.length; i++) {
            heights.push((elements[i] as HTMLElement).offsetHeight);
          }

          // All rows must have equal height
          const firstHeight = heights[0];
          for (let i = 1; i < heights.length; i++) {
            if (heights[i] !== firstHeight) {
              return false;
            }
          }

          // Height equals that of any single non-empty row rendered alone
          container.innerHTML = '';
          const singleDiv = document.createElement('div');
          singleDiv.className = 'view-row';
          singleDiv.textContent = viewRows[0];
          container.appendChild(singleDiv);
          const singleHeight = (singleDiv as HTMLElement).offsetHeight;

          return firstHeight === singleHeight;
        }
      ),
      { numRuns: 10 }
    );
  });
});
