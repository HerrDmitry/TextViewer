# Implementation Plan

- [x] 1. Write bug condition exploration test
  - **Property 1: Bug Condition** - Empty Rows Collapse to Zero Height
  - **CRITICAL**: This test MUST FAIL on unfixed code — failure confirms the bug exists
  - **DO NOT attempt to fix the test or the code when it fails**
  - **NOTE**: This test encodes the expected behavior — it will validate the fix when it passes after implementation
  - **GOAL**: Surface counterexamples that demonstrate empty-string rows collapse to 0px height
  - **Scoped PBT Approach**: Generate viewRows arrays containing empty strings (simulating short lines scrolled past); assert that `.view-row` elements always have non-zero height equal to one line-height unit
  - Test file: `ClientApp/src/app/shell/text-view-area/text-view-area.css-bug-condition.property.spec.ts`
  - Framework: Jest + fast-check, `{ numRuns: 10 }`
  - Use JSDOM with a `<style>` block containing the current `.view-row` CSS (copy from `text-view-area.component.css`)
  - Create DOM elements: `<div class="view-row">{{row}}</div>` for each generated row
  - Property: for all viewRows where at least one row is `""`, every `.view-row` element should have `offsetHeight > 0` and equal height
  - Bug Condition from design: `startCol >= lineContent.length` → row content is `""`
  - Run: `cd ClientApp && npx jest text-view-area.css-bug-condition`
  - **EXPECTED OUTCOME**: Test FAILS (empty divs have 0 offsetHeight in JSDOM with `white-space: pre` and no min-height)
  - Document counterexamples: empty-string rows render to 0px height
  - _Requirements: 1.1, 1.2_

- [x] 2. Write preservation property tests (BEFORE implementing fix)
  - **Property 2: Preservation** - Non-Empty Rows Unchanged
  - **IMPORTANT**: Follow observation-first methodology
  - Test file: `ClientApp/src/app/shell/text-view-area/text-view-area.css-preservation.property.spec.ts`
  - Framework: Jest + fast-check, `{ numRuns: 10 }`
  - Observe: non-empty `.view-row` elements render with consistent height determined by font metrics on UNFIXED code
  - Write property-based test: for all viewRows where every row is non-empty, all `.view-row` elements have equal `offsetHeight` and that height equals the height of any single non-empty row
  - Use same JSDOM + `<style>` approach as task 1
  - Property: for all arrays of non-empty strings, every `.view-row` has identical height (font-metrics consistency)
  - Run: `cd ClientApp && npx jest text-view-area.css-preservation`
  - **EXPECTED OUTCOME**: Tests PASS (non-empty rows already render correctly)
  - Mark task complete when tests are written, run, and passing on unfixed code
  - _Requirements: 3.1, 3.2, 3.3, 3.4_

- [x] 3. Fix for short-line horizontal scroll collapse

  - [x] 3.1 Implement the CSS fix
    - File: `ClientApp/src/app/shell/text-view-area/text-view-area.component.css`
    - Selector: `.view-row`
    - Add property: `min-height: 1lh;`
    - This ensures empty-content rows occupy one line-height of vertical space
    - _Bug_Condition: isBugCondition(input) where startCol >= lineContent.length → row content is ""_
    - _Expected_Behavior: all .view-row elements maintain 1lh minimum height regardless of content_
    - _Preservation: non-empty rows unaffected because their content already generates a line box >= 1lh_
    - _Requirements: 2.1, 2.2, 3.1, 3.2, 3.3, 3.4_

  - [x] 3.2 Verify bug condition exploration test now passes
    - **Property 1: Expected Behavior** - Empty Rows Maintain Height
    - **IMPORTANT**: Re-run the SAME test from task 1 — do NOT write a new test
    - Run: `cd ClientApp && npx jest text-view-area.css-bug-condition`
    - **EXPECTED OUTCOME**: Test PASSES (confirms bug is fixed — empty rows now have min-height: 1lh)
    - _Requirements: 2.1, 2.2_

  - [x] 3.3 Verify preservation tests still pass
    - **Property 2: Preservation** - Non-Empty Rows Unchanged
    - **IMPORTANT**: Re-run the SAME tests from task 2 — do NOT write new tests
    - Run: `cd ClientApp && npx jest text-view-area.css-preservation`
    - **EXPECTED OUTCOME**: Tests PASS (confirms no regressions — non-empty rows unaffected by min-height addition)

- [x] 4. Checkpoint - Ensure all tests pass
  - Run full test suite: `cd ClientApp && npx jest`
  - Ensure all tests pass, ask the user if questions arise.
