#[[file:.kiro/specs/_global/design-shared.md]]

# Get Byte Offset Improvement — Bugfix Design

## Overview

`LineIndex.GetByteOffset(int lineIndex)` currently computes offsets by iterating all prior lines and performing per-line segment lookups. This is correct but too slow for large files and near-EOF requests.

The fix introduces fast offset computation using segment-aware prefix metadata:
- segment-level cumulative byte prefix sums,
- optional per-segment intra-prefix byte sums (or equivalent fast local summation),
- optional sequential-access cursor cache for repeated adjacent queries.

All returned offsets and existing scan/read semantics remain unchanged.

## Glossary

- **Bug_Condition (C)**: `GetByteOffset` uses per-line accumulation from `0..lineIndex-1` with repeated segment lookup
- **Property (P)**: `GetByteOffset` returns identical offsets while avoiding linear scan over all preceding lines
- **Preservation**: Existing line-length correctness, char-length publication semantics, thread-safety guarantees, and clear/reset behavior remain unchanged
- **Segment Prefix Bytes**: Cumulative bytes before each segment, enabling direct base offset retrieval
- **Intra-Segment Prefix**: Cumulative byte sums inside a segment, enabling fast partial-segment offset
- **Sequential Cursor Cache**: Small cache for `(lastLineIndex, lastOffset, lastSegment)` to accelerate adjacent queries

## Bug Details

### Bug Condition

The bug manifests when offset queries target deep line indices in large files. Current implementation performs repeated summation from the beginning for each query, and each step performs segment search/read, causing high cumulative CPU cost.

**Formal Specification:**
```
FUNCTION isBugCondition(input)
  INPUT: input of type GetByteOffsetRequest
  OUTPUT: boolean

  RETURN input.lineIndex is valid
         AND lineIndex is large
         AND implementation performs per-line accumulation from 0
END FUNCTION
```

### Examples

- File with 2M lines, query `GetByteOffset(1_900_000)` repeatedly during viewport rendering → visible delay due to large repeated prefix scans
- Sequential viewport requests for lines `[100000..100200]` → each call recomputes most of the same prefix work
- File with many small segments → repeated `FindSegment(i)` cost compounds inside loop

## Expected Behavior

### Preservation Requirements

**Unchanged Behaviors:**
- `GetByteOffset(0) == 0`
- `GetByteOffset(LineCount) == file size`
- For all valid `i`: `GetByteOffset(i) == Sum(GetByteLength(0..i-1))`
- `GetByteLength` and `GetCharLength` values and semantics remain unchanged
- `AppendByteLengths`, `SetCharLength`, `FinalizeCharLengths`, and `Clear` retain current correctness behavior
- Existing concurrency model (single writer, multi-reader) remains valid

**Scope:**
Only byte-offset computation path is optimized. No protocol format, no file-view API contract, and no char-length behavior changes are introduced.

## Hypothesized Root Cause

1. **Algorithmic mismatch**: offset queries are answered using repeated prefix scan from line 0 rather than indexable cumulative metadata
2. **Nested lookup overhead**: per-line loop invokes segment lookup repeatedly, adding extra logarithmic cost
3. **No locality reuse**: adjacent offset queries do not reuse prior results even though access patterns are often sequential

## Correctness Properties

Property 1: Bug Condition - Offset Correctness with Fast Path

_For any_ valid line index and any index state, optimized `GetByteOffset` SHALL return exactly the same value as baseline cumulative sum of byte lengths.

**Validates: Requirements 2.1, 2.4**

Property 2: Bug Condition - Improved Access Cost Characteristics

_For any_ valid query, optimized implementation SHALL avoid per-line global prefix traversal from line 0 and use segment-indexed metadata.

**Validates: Requirements 2.2, 2.3**

Property 3: Preservation - Non-Offset Behavior Unchanged

_For any_ sequence of append/set-char/finalize/clear operations, all non-offset observable behaviors SHALL match existing implementation.

**Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5, 3.6**

## Data Structure & Algorithm Plan

### Option A (Recommended): Segment Prefix Index + Fast Tail Sum

Maintain metadata in `SegmentDirectory` / `LineIndex`:
- `segmentStartLines[]` (already implicit via segments)
- `segmentPrefixBytes[]`: total bytes before each segment
- optionally `segmentTotalBytes[]` (or derive from intra-prefix tail)

`GetByteOffset(lineIndex)`:
1. Validate bounds
2. If `lineIndex == 0`, return `0`
3. Find containing segment once (`O(log S)`)
4. `baseOffset = segmentPrefixBytes[segmentIndex]`
5. `tailOffset = sum bytes inside segment up to local index`
6. Return `baseOffset + tailOffset`

Complexity target: `O(log S + tailWork)` where `tailWork` is constant or bounded small if intra-prefix is stored.

### Option B: Add Intra-Segment Prefix Arrays

For each segment, maintain cumulative byte sums per line position:
- `intraPrefix[k] = sum(byteLen[0..k-1])`

Then `tailOffset` in step 5 is O(1), reducing query to `O(log S)`.

Trade-off: extra memory per segment, more append-time work.

### Option C (Optional): Sequential Cursor Cache

Cache most recent query state for locality:
- `(lastLineIndex, lastOffset, segmentRef, localOffset)`

If next query is adjacent/nearby, compute incrementally with minimal reads.

Trade-off: additional complexity; keep as optional phase after prefix index.

## Concurrency & Publication Considerations

- Metadata updates occur in writer path guarded by existing `_writeLock`
- Readers continue lock-free reads; publish order must ensure new segments and their prefix metadata are visible before `_lineCount` increases
- `SetCharLength` remains independent (byte offsets depend only on byte lengths)
- `Clear()` resets all metadata atomically under lock

## Validation Plan

### Functional

- Existing offset invariants across randomized byte-length inputs
- Edge cases: empty index, first line, last line, `LineCount`, segment boundaries
- Preservation checks for byte length/char length APIs and clear/reset behavior

### Performance

Benchmarks before/after:
- Near-EOF single queries on large synthetic datasets
- Random offset queries across whole range
- Sequential window queries simulating view rendering

Success criteria:
- Significant speedup for near-EOF queries (target: order-of-magnitude class improvement)
- No correctness regressions
