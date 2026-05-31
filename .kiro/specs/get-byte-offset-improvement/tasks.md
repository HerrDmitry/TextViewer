# Implementation Plan

## Overview

Improve `LineIndex.GetByteOffset` performance for large files by replacing per-line prefix scanning with segment-indexed offset computation, while preserving all existing correctness and concurrency behavior.

## Tasks

- [ ] 1. Write bug condition benchmark/reproducer (BEFORE implementing fix)
  - **Property 1: Bug Condition** - Near-EOF offset queries are too slow on current implementation
  - **CRITICAL**: Measure on unfixed code first to establish baseline
  - Add a focused benchmark or perf test that repeatedly calls `GetByteOffset` on large synthetic indexes (including near-EOF indices)
  - Capture baseline metrics for:
    - Near-EOF queries
    - Random queries
    - Sequential window queries
  - Document observed cost trend with increasing `lineIndex`
  - _Requirements: 1.1, 1.2, 1.3_

- [ ] 2. Write preservation tests (BEFORE implementing fix)
  - **Property 3: Preservation** - Existing behavior unchanged
  - Verify (or add tests for) invariants on unfixed code:
    - `GetByteOffset(0) == 0`
    - `GetByteOffset(LineCount) == file size`
    - `GetByteOffset(i) == sum(ByteLength[0..i-1])` across randomized inputs
    - Behavior at segment boundaries remains correct
    - `GetByteLength`, `GetCharLength`, scan publication semantics, and `Clear()` unchanged
  - Ensure these tests pass on unfixed code to lock baseline behavior
  - _Requirements: 2.1, 2.4, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6_

- [ ] 3. Implement optimized offset strategy

  - [ ] 3.1 Add segment-level prefix metadata
    - Extend index internals to store cumulative byte offset before each segment
    - Ensure metadata is built/updated during append under existing writer lock
    - _Requirements: 2.2, 2.3_

  - [ ] 3.2 Refactor `GetByteOffset` to use segment-indexed path
    - Replace global per-line loop with:
      - single segment lookup for target line,
      - prefix-before-segment retrieval,
      - partial sum within target segment
    - Preserve all existing bounds and return-value semantics
    - _Requirements: 2.1, 2.2, 2.4_

  - [ ] 3.3 Optional: add intra-segment prefix cache
    - If profiling indicates tail summation is still material, add per-segment intra-prefix sums for O(1) local offset
    - Keep memory overhead documented and bounded
    - _Requirements: 2.2, 2.3_

  - [ ] 3.4 Optional: add sequential-access cursor cache
    - Add tiny cache to accelerate adjacent line queries common in viewport rendering
    - Ensure correctness for non-sequential/random access remains unchanged
    - _Requirements: 2.3, 3.6_

  - [ ] 3.5 Ensure clear/reset/publication behavior is preserved
    - Reset any new metadata in `Clear()`
    - Verify metadata publication happens before `_lineCount` visibility for readers
    - _Requirements: 3.3, 3.5, 3.6_

- [ ] 4. Verify bug-condition benchmark and preservation tests after fix
  - Re-run the same benchmark from task 1
  - **Expected outcome**: substantial speedup, especially near EOF
  - Re-run the same tests from task 2
  - **Expected outcome**: all correctness/preservation tests remain green
  - _Requirements: 2.1, 2.2, 2.3, 2.4, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6_

- [ ] 5. Checkpoint - Ensure no regressions
  - Run targeted service tests covering `LineIndex` and integration points using `GetByteOffset`
  - If risk is medium/high after profiling-driven changes, run broader related suite
  - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6_

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1", "2"] },
    { "id": 1, "tasks": ["3.1", "3.2"] },
    { "id": 2, "tasks": ["3.3", "3.4", "3.5"] },
    { "id": 3, "tasks": ["4"] },
    { "id": 4, "tasks": ["5"] }
  ]
}
```

## Notes

- Keep benchmark dataset deterministic and representative (e.g., fixed seed for generated line lengths)
- Prefer comparing median/p95 timings rather than single-run wall-clock values
- Do not weaken or remove existing correctness tests to satisfy performance goals
- If optional tasks 3.3/3.4 are skipped, document why baseline optimization is sufficient
