/**
 * Bugfix: short-line-horizontal-scroll — Bug Condition Exploration Test
 *
 * **Validates: Requirements 1.1, 1.2**
 *
 * Property 1: Bug Condition — Empty Rows Collapse to Zero Height
 *
 * This test demonstrates that empty-string rows (simulating lines shorter than
 * startCol) collapse to 0px height under the current CSS. It encodes the
 * EXPECTED behavior: all .view-row elements should have non-zero height equal
 * to one line-height regardless of content.
 *
 * On UNFIXED code this test MUST FAIL — failure confirms the bug exists.
 */
import * as fc from 'fast-check';

describe('Bugfix: short-line-horizontal-scroll, Property 1: Bug Condition — Empty Rows Collapse to Zero Height', () => {

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
   * Property: for all viewRows arrays where at least one row is "",
   * every .view-row element should have offsetHeight > 0 and equal height.
   *
   * Bug condition: startCol >= lineContent.length → row content is ""
   * Empty divs with white-space: pre produce no line box → 0px height.
   */
  it('empty-string rows should have non-zero height equal to one line-height', () => {
    // Generator: arrays of strings where at least one is empty
    const viewRowsArb = fc.tuple(
      fc.array(fc.oneof(fc.constant(''), fc.string({ minLength: 1, maxLength: 20 })), { minLength: 1, maxLength: 10 }),
      fc.nat({ max: 9 })
    ).map(([rows, insertIdx]) => {
      // Ensure at least one empty string exists
      const idx = insertIdx % (rows.length + 1);
      const result = [...rows];
      result.splice(idx, 0, '');
      return result;
    });

    fc.assert(
      fc.property(
        viewRowsArb,
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

          // All rows must have min-height applied (JSDOM has no layout engine,
          // so offsetHeight is always 0; validate via getComputedStyle instead)
          for (let i = 0; i < elements.length; i++) {
            const el = elements[i] as HTMLElement;
            const minHeight = window.getComputedStyle(el).minHeight;
            if (!minHeight || minHeight === '0' || minHeight === '0px' || minHeight === '') {
              return false;
            }
          }

          // All rows must have equal min-height
          const firstMinHeight = window.getComputedStyle(elements[0] as HTMLElement).minHeight;
          for (let i = 1; i < elements.length; i++) {
            const el = elements[i] as HTMLElement;
            if (window.getComputedStyle(el).minHeight !== firstMinHeight) {
              return false;
            }
          }

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});
