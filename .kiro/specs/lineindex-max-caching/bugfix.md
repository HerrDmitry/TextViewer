# Bugfix Requirements Document

## Introduction

`HandleGetScrollInfo` iterates ALL indexed lines on every `get-scroll-info` request to compute max byte/char lengths — O(N) per call. `LineIndex` should cache these maximums incrementally during scan operations, exposing O(1) reads.

## Bug Analysis

### Current Behavior (Defect)

1.1 WHEN a `get-scroll-info` request is received THEN the system iterates all N lines in `LineIndex` to compute `maxByteLength`, making the operation O(N)

1.2 WHEN a `get-scroll-info` request is received THEN the system iterates all N lines in `LineIndex` to compute `maxCharLength`, making the operation O(N)

1.3 WHEN multiple `get-scroll-info` requests arrive during an active scan THEN the system performs redundant full iterations each time, wasting CPU proportional to file size

### Expected Behavior (Correct)

2.1 WHEN a `get-scroll-info` request is received THEN the system SHALL return `MaxByteLength` in O(1) by reading a cached field on `LineIndex`

2.2 WHEN a `get-scroll-info` request is received THEN the system SHALL return `MaxCharLength` in O(1) by reading a cached field on `LineIndex`

2.3 WHEN `MaxCharLength` is queried before any char lengths have been written THEN the system SHALL return null (no char scan data available yet)

### Unchanged Behavior (Regression Prevention)

3.1 WHEN `AppendByteLengths` is called with a span of byte lengths THEN the system SHALL CONTINUE TO correctly store all byte lengths and increment `LineCount`

3.2 WHEN `SetCharLength` is called for a line THEN the system SHALL CONTINUE TO correctly store the char length and increment `_charLengthsWrittenUpTo`

3.3 WHEN `GetByteLength(i)` is called THEN the system SHALL CONTINUE TO return the exact byte length for line i

3.4 WHEN `GetCharLength(i)` is called for a line where char length has been written THEN the system SHALL CONTINUE TO return the exact char length for that line

3.5 WHEN `Clear()` is called THEN the system SHALL CONTINUE TO reset the index to empty state

3.6 WHEN `HandleGetScrollInfo` is called THEN the system SHALL CONTINUE TO return the same `scanState\nlineCount\nmaxByteLength\nmaxCharLength` response format with correct values
