# Testing — Property-Based Tests

## Iteration Cap

**RULE: All property-based tests MUST use no more than 10 iterations.**

| Framework | Setting |
|-----------|---------|
| fast-check (TS) | `{ numRuns: 10 }` |
| FsCheck (C#) | `[Property(MaxTest = 10)]` |

This overrides any "Minimum 100 iterations" stated in spec/design/task docs.
Existing tests are grandfathered — do not bulk-update. Apply cap to all new/modified property tests.
