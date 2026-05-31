# Implementation Plan

## Overview

Move line number computation from frontend to backend. Backend responses include per-row line number metadata; frontend displays these directly, eliminating race conditions and state inconsistency bugs in gutter number rendering.

## Tasks

- [x] 1. Write bug condition exploration test
  - **Property 1: Bug Condition** - Line Numbers Out of Sync with Displayed Content
  - **CRITICAL**: This test MUST FAIL on unfixed code - failure confirms the bug exists
  - **DO NOT attempt to fix the test or the code when it fails**
  - **NOTE**: This test encodes the expected behavior - it will validate the fix when it passes after implementation
  - **GOAL**: Surface counterexamples that demonstrate gutter numbers drift from actual displayed content
  - **Scoped PBT Approach**: Scope the property to concrete failing cases:
    - Non-wrapped: simulate get-view response arriving after `startLine` has been incremented by scroll → `activeGutterNumbers` produces values offset from actual response rows
    - Wrapped: set `rawResponseContent` to content fetched at characterOffset=0, then update characterOffset before computed signal evaluates → gutter numbers wrong
    - Wrapped resize: change `colCount` after caching `rawResponseContent` → `computeWrappedGutterNumbers` splits content differently than actual displayed rows
  - Test that `activeGutterNumbers` matches the rows actually present in the response (from Bug Condition in design: `isBugCondition(input) = response does NOT include per-row line number annotations AND frontend computes gutter numbers from local state`)
  - Expected behavior assertions: displayed gutter numbers = response-provided line numbers (from Expected Behavior in design)
  - Run test on UNFIXED code - expect FAILURE (this confirms the bug exists)
  - Document counterexamples found (e.g., "activeGutterNumbers returns [6,7,8,...] when displayed rows correspond to lines [4,5,6,...]")
  - Mark task complete when test is written, run, and failure is documented
  - _Requirements: 1.1, 1.2, 1.3, 1.4_

- [x] 2. Write preservation property tests (BEFORE implementing fix)
  - **Property 2: Preservation** - Non-Gutter Behavior Unchanged
  - **IMPORTANT**: Follow observation-first methodology
  - Observe on UNFIXED code:
    - `computeGutterWidth(totalLines, charWidth)` produces correct width for various inputs
    - Non-wrapped request format remains `viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount`
    - Wrapped request format remains `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount`
    - Error responses still display error message and keep previous rows visible
    - Scrollbar verticalMax/horizontalMax computation unchanged
    - `splitIntoVisualRows` function behavior unchanged
  - Write property-based tests capturing observed behavior:
    - For all (totalLines, charWidth) pairs: `computeGutterWidth` returns `digitCount(totalLines) * charWidth + 16`
    - For all valid request params: non-wrapped request format is `viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount`
    - For all valid wrapped params: wrapped request format is `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount`
    - For all (content, colCount) pairs: `splitIntoVisualRows` produces same row splits
  - Verify tests pass on UNFIXED code
  - Mark task complete when tests are written, run, and passing on unfixed code
  - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7_

- [x] 3. Fix: Move line number computation to backend and simplify frontend

  - [x] 3.1 Backend: Extend `GetViewAsync` to include line numbers in response
    - In `Services/FileViewService.cs`, modify `GetViewAsync` to return line number metadata alongside rows
    - Add `IReadOnlyList<int> LineNumbers` to `ViewResult` — parallel array containing `startLine + i + 1` for each row `i`
    - _Bug_Condition: isBugCondition(input) = response does NOT include per-row line number annotations_
    - _Expected_Behavior: response includes line number for each row, displayed gutter numbers = response-provided line numbers_
    - _Preservation: Request format unchanged (viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount)_
    - _Requirements: 2.1_

  - [x] 3.2 Backend: Extend `GetWrappedViewAsync` to include per-visual-row line numbers
    - In `Services/FileViewService.cs`, modify `GetWrappedViewAsync` to track which logical line each character belongs to during extraction
    - Return `WrappedViewResult` with `Content` (string) and `LineNumbers` (list of nullable ints, one per visual row)
    - First visual row of each logical line gets the 1-based line number; continuation rows get null
    - Assert invariant: `LineNumbers.Count == visual row count from col-count splitting`
    - _Bug_Condition: isBugCondition(input) = response does NOT include per-row line number annotations_
    - _Expected_Behavior: backend determines which rows get a number and which get null (continuation rows)_
    - _Preservation: Request format unchanged (viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount)_
    - _Requirements: 2.2, 2.3_

  - [x] 3.3 Backend: Update `HandleGetView` response format (non-wrapped)
    - In `Program.cs`, modify non-wrapped response serialization to prefix each row with `{lineNum}\t{rowContent}`
    - Frontend splits on first `\t` only — content after first tab is verbatim row content
    - _Bug_Condition: isBugCondition(input) = response does NOT include per-row line number annotations_
    - _Expected_Behavior: each row in response carries its line number_
    - _Preservation: Row content itself unchanged, only transport format adds prefix_
    - _Requirements: 2.1_

  - [x] 3.4 Backend: Update `HandleGetView` response format (wrapped)
    - In `Program.cs`, modify wrapped response serialization to prepend `L:{n1},{n2},{n3},...\n` header before content
    - Each `n` is either a 1-based line number or empty string (for continuation rows)
    - _Bug_Condition: isBugCondition(input) = response does NOT include per-row line number annotations_
    - _Expected_Behavior: wrapped response carries per-visual-row line number header_
    - _Preservation: Content string after header unchanged_
    - _Requirements: 2.2, 2.3, 2.4_

  - [x] 3.5 Frontend: Parse line numbers from response in `handleViewResponse`
    - In `shell-state.service.ts`, update `handleViewResponse` for non-wrapped mode: split each row on first `\t` → extract lineNum (parseInt) and row content; store line numbers in `gutterNumbers` field on `TabViewState`
    - For wrapped mode: extract `L:...` header line (first `\n`), parse comma-separated values into `(number | null)[]`; store in `gutterNumbers`; pass remaining content to `splitIntoVisualRows`
    - If malformed (no `\t` in non-wrapped, no `L:` prefix in wrapped) → log error, keep previous state
    - _Bug_Condition: isBugCondition(input) = response does NOT include per-row line number annotations_
    - _Expected_Behavior: frontend displays backend-provided line numbers directly_
    - _Preservation: Row content display unchanged, splitIntoVisualRows still used for wrapped rendering_
    - _Requirements: 2.1, 2.2, 2.3, 2.4_

  - [x] 3.6 Frontend: Add `gutterNumbers` field to `TabViewState`
    - In `shell.types.ts`, add `gutterNumbers: (number | null)[] | null` to `TabViewState` interface
    - Initialize to `null` on tab creation
    - _Requirements: 2.1, 2.2_

  - [x] 3.7 Frontend: Simplify `activeGutterNumbers` signal
    - In `shell-state.service.ts`, replace computation logic in `activeGutterNumbers` with direct read: `state.gutterNumbers ?? []`
    - Remove calls to `computeNonWrappedLineNumbers` and `computeWrappedGutterNumbers`
    - _Bug_Condition: isBugCondition(input) = frontend computes gutter numbers from local state_
    - _Expected_Behavior: gutter numbers come directly from backend response, no recomputation_
    - _Requirements: 2.1, 2.2, 2.3, 2.4_

  - [x] 3.8 Frontend: Remove dead code
    - Delete `computeNonWrappedLineNumbers` and `computeWrappedGutterNumbers` from `line-wrap-utils.ts`
    - Remove `rawResponseContent` field from `TabViewState` and all code that populates it
    - _Requirements: 2.1, 2.2_

  - [x] 3.9 Update backend tests to use new response formats
    - In `TextViewer.Tests/BackendHandlerTests.cs`, update all `HandleGetView` assertions to expect `{lineNum}\t{content}` format (non-wrapped) and `L:` header (wrapped)
    - Verify all existing test scenarios still validate correct behavior with new format
    - _Preservation: All existing test scenarios still covered, only assertion format changes_
    - _Requirements: 2.1, 2.2, 3.2, 3.3_

  - [x] 3.10 Update frontend tests to use new response formats
    - In `shell-state.service.spec.ts`, `shell-state.text-handling.spec.ts`, and property spec files: update all mock responses to include tab-prefixed line numbers (non-wrapped) and `L:` headers (wrapped)
    - Verify all existing test scenarios still pass with new mock format
    - _Preservation: All existing test scenarios still covered, only mock format changes_
    - _Requirements: 2.1, 2.2, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7_

  - [x] 3.11 Verify bug condition exploration test now passes
    - **Property 1: Expected Behavior** - Line Numbers Match Displayed Content
    - **IMPORTANT**: Re-run the SAME test from task 1 - do NOT write a new test
    - The test from task 1 encodes the expected behavior
    - When this test passes, it confirms the expected behavior is satisfied
    - Run bug condition exploration test from step 1
    - **EXPECTED OUTCOME**: Test PASSES (confirms bug is fixed)
    - _Requirements: 2.1, 2.2, 2.3, 2.4_

  - [x] 3.12 Verify preservation tests still pass
    - **Property 2: Preservation** - Non-Gutter Behavior Unchanged
    - **IMPORTANT**: Re-run the SAME tests from task 2 - do NOT write new tests
    - Run preservation property tests from step 2
    - **EXPECTED OUTCOME**: Tests PASS (confirms no regressions)
    - Confirm all tests still pass after fix (no regressions)
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7_

- [x] 4. Checkpoint - Ensure all tests pass
  - Run full test suite: `dotnet test` (backend) and `cd ClientApp && npx jest --run` (frontend)
  - Ensure all tests pass, ask the user if questions arise.

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1", "2"] },
    { "id": 1, "tasks": ["3.1", "3.2", "3.3", "3.4"] },
    { "id": 2, "tasks": ["3.5", "3.6"] },
    { "id": 3, "tasks": ["3.7", "3.8"] },
    { "id": 4, "tasks": ["3.9", "3.10"] },
    { "id": 5, "tasks": ["3.11", "3.12"] },
    { "id": 6, "tasks": ["4"] }
  ]
}
```

## Notes

- Property-based tests use `{ numRuns: 10 }` (fast-check) per workspace testing policy
- Backend tests use xUnit (`dotnet test`); frontend tests use Jest (`npx jest`)
- The exploration test (task 1) is expected to FAIL on unfixed code — this confirms the bug exists
- The preservation test (task 2) is expected to PASS on unfixed code — this captures baseline behavior
- After implementation, exploration test should PASS and preservation test should still PASS
