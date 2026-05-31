# Bugfix Requirements Document

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

`LineIndex.GetByteOffset(int lineIndex)` is currently slow for large files, especially when `lineIndex` is near the end of file. The method iterates from line `0` to `lineIndex - 1` and repeatedly calls segment lookup per line, causing high cumulative cost in common viewer workflows.

The fix introduces a faster offset computation strategy based on segment-level prefix sums (and optional sequential-access caching), while preserving all existing correctness and concurrency guarantees.

## Bug Analysis

### Current Behavior (Defect)

1.1 WHEN `GetByteOffset(lineIndex)` is called with `lineIndex` near `LineCount` on a large file THEN the system SHALL take time proportional to scanning almost all prior lines, causing visible latency

1.2 WHEN `GetByteOffset` is called repeatedly for nearby line indices during viewport rendering THEN the system SHALL repeatedly recompute cumulative sums from the beginning, causing avoidable CPU overhead

1.3 WHEN the file has many segments THEN per-line `FindSegment` lookups inside the current loop SHALL compound lookup cost

### Expected Behavior (Correct)

2.1 WHEN `GetByteOffset(lineIndex)` is called for any valid line index THEN the system SHALL return the same exact offset value as before

2.2 WHEN `GetByteOffset(lineIndex)` is called near end-of-file on very large indexes THEN the system SHALL execute in sublinear time with respect to `lineIndex` (target: segment-index-based lookup)

2.3 WHEN `GetByteOffset` is called repeatedly for nearby lines THEN the system SHALL avoid full prefix recomputation on every call

2.4 WHEN `GetByteOffset(0)` and `GetByteOffset(LineCount)` are called THEN the system SHALL continue returning `0` and file size respectively

### Unchanged Behavior (Regression Prevention)

3.1 WHEN `GetByteLength(i)` is called THEN returned byte lengths SHALL remain unchanged

3.2 WHEN `GetCharLength(i)` and char-length publication semantics are used THEN behavior SHALL remain unchanged

3.3 WHEN Quick_Scan appends line lengths THEN append correctness and thread-safety SHALL remain unchanged

3.4 WHEN Full_Scan updates char lengths THEN atomic write and publication ordering SHALL remain unchanged

3.5 WHEN `Clear()` is called THEN all index state SHALL reset correctly

3.6 WHEN concurrent readers query `LineIndex` during scanning THEN existing reader safety guarantees SHALL remain unchanged

---

## Bug Condition (Formal)

```pascal
FUNCTION isBugCondition(X)
  INPUT: X of type GetByteOffsetRequest
  OUTPUT: boolean

  RETURN X.lineIndex is valid
         AND X.lineIndex is large (near LineCount)
         AND implementation performs per-line accumulation from 0..lineIndex-1
END FUNCTION
```

```pascal
// Property: Fix Checking — correctness + improved asymptotic behavior
FOR ALL X WHERE isBugCondition(X) DO
  baseline ← Sum(ByteLength[0..X.lineIndex-1])
  actual ← GetByteOffset'(X.lineIndex)
  ASSERT actual = baseline
  ASSERT implementation does NOT perform per-line segment lookup from line 0
END FOR
```

```pascal
// Property: Preservation Checking
FOR ALL X WHERE NOT isBugCondition(X) DO
  ASSERT F(X) = F'(X)
  // Byte/char length reads, scan publication semantics, and reset behavior unchanged
END FOR
```
