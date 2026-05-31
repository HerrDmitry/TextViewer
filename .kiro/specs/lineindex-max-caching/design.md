# LineIndex Max Caching Bugfix Design

## Overview

`HandleGetScrollInfo` performs O(N) iteration over all lines to find max byte/char lengths on every call. Fix: cache maximums incrementally inside `LineIndex` during `AppendByteLengths` and `SetCharLength`, expose O(1) properties, update `HandleGetScrollInfo` to use them.

## Glossary

- **Bug_Condition (C)**: Any call to `HandleGetScrollInfo` that triggers O(N) iteration over `LineIndex` to compute max lengths
- **Property (P)**: `MaxByteLength` and `MaxCharLength` properties return correct maximums in O(1)
- **Preservation**: All existing `LineIndex` read/write behavior (byte lengths, char lengths, line count, offsets) unchanged
- **`_maxByteLength`**: Volatile field tracking running maximum of all appended byte lengths
- **`_maxCharLength`**: Volatile field tracking running maximum of all written char lengths

## Bug Details

### Bug Condition

The bug manifests on every `get-scroll-info` request. `HandleGetScrollInfo` iterates all `lineCount` lines calling `GetByteLength(i)` and `GetCharLength(i)` to find maximums — O(N) per request, repeated on every poll.

**Formal Specification:**
```
FUNCTION isBugCondition(request)
  INPUT: request of type GetScrollInfoRequest
  OUTPUT: boolean
  
  RETURN request.type = "get-scroll-info"
         AND lineIndex.LineCount > 0
END FUNCTION
```

### Examples

- File with 1M lines: each `get-scroll-info` iterates 1M lines → visible UI lag
- File with 100 lines: iteration is fast but still wasteful vs O(1) read
- During active scan: repeated requests each re-iterate growing line count
- After scan complete: max values are stable but still recomputed every request

## Expected Behavior

### Preservation Requirements

**Unchanged Behaviors:**
- `AppendByteLengths` stores byte lengths correctly and increments `LineCount`
- `SetCharLength` stores char lengths correctly and increments `_charLengthsWrittenUpTo`
- `GetByteLength(i)` returns exact byte length for line i
- `GetCharLength(i)` returns exact char length or null
- `GetByteOffset(i)` returns correct cumulative offset
- `Clear()` resets to empty state
- `HandleGetScrollInfo` response format: `scanState\nlineCount\nmaxByteLength\nmaxCharLength`

**Scope:**
All `LineIndex` operations other than max-length computation are unaffected. The fix only adds incremental tracking and replaces the iteration in `HandleGetScrollInfo` with property reads.

## Hypothesized Root Cause

1. **Missing cached state**: `LineIndex` has no fields tracking maximums — the only way to get max is iteration
2. **Design oversight**: Original implementation deferred optimization; acceptable for small files but O(N) per request is problematic at scale
3. **No incremental update hook**: `AppendByteLengths` and `SetCharLength` don't update any running max

## Correctness Properties

Property 1: Bug Condition - MaxByteLength Equals Iteration Maximum

_For any_ `LineIndex` state after one or more `AppendByteLengths` calls, `MaxByteLength` SHALL equal the maximum value across all stored byte lengths (same result as iterating all lines).

**Validates: Requirements 2.1**

Property 2: Bug Condition - MaxCharLength Equals Iteration Maximum

_For any_ `LineIndex` state after one or more `SetCharLength` calls, `MaxCharLength` SHALL equal the maximum value across all stored char lengths (same result as iterating all written char lengths). When no char lengths have been written, `MaxCharLength` SHALL be null.

**Validates: Requirements 2.2, 2.3**

Property 3: Preservation - Existing LineIndex Behavior Unchanged

_For any_ sequence of `AppendByteLengths` and `SetCharLength` operations, `GetByteLength(i)`, `GetCharLength(i)`, `GetByteOffset(i)`, and `LineCount` SHALL produce identical results to the unfixed code.

**Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5, 3.6**

## Fix Implementation

### Changes Required

**File**: `Services/LineIndex.cs`

**Fields to add:**
```csharp
private volatile ulong _maxByteLength;
private volatile ulong _maxCharLength;
```

**Properties to add:**
```csharp
public ulong MaxByteLength => _maxByteLength;
public ulong? MaxCharLength => _charLengthsWrittenUpTo == 0 ? null : _maxCharLength;
```

**Specific Changes:**

1. **`AppendByteLengths`**: After appending to segments, iterate the incoming `byteLengths` span to find local max, compare with `_maxByteLength`, update if larger. This happens inside the existing `_writeLock`.

2. **`SetCharLength`**: After writing char length to segment, compare `charLength` with `_maxCharLength`, update if larger. The volatile write of `_maxCharLength` must happen before the volatile write of `_charLengthsWrittenUpTo` (existing ordering guarantees this since both are volatile writes in sequence).

3. **`Clear()`**: Reset `_maxByteLength = 0` and `_maxCharLength = 0` inside existing lock.

4. **`MaxCharLength` nullability**: Return `null` when `_charLengthsWrittenUpTo == 0` (no char lengths written yet), otherwise return `_maxCharLength`.

---

**File**: `Program.cs`

**Function**: `HandleGetScrollInfo`

**Change**: Replace the O(N) iteration loop with:
```csharp
var maxByteLength = lineIndex.MaxByteLength;
var maxCharLength = lineIndex.MaxCharLength ?? 0;
```

## Testing Strategy

### Validation Approach

Two-phase: first surface counterexamples showing cached max diverges from iteration max on unfixed code (property will fail because properties don't exist yet), then verify fix produces correct cached values and preserves all existing behavior.

### Exploratory Bug Condition Checking

**Goal**: Demonstrate that `LineIndex` lacks `MaxByteLength`/`MaxCharLength` properties — the only way to get max is O(N) iteration.

**Test Plan**: Write property-based tests asserting `MaxByteLength == max(all byte lengths)` and `MaxCharLength == max(all char lengths)`. On unfixed code these properties don't exist → compilation failure confirms bug.

**Test Cases**:
1. Append random byte lengths → assert `MaxByteLength` equals max of appended values
2. Set random char lengths → assert `MaxCharLength` equals max of set values
3. Multiple appends → assert `MaxByteLength` tracks global max across all appends
4. No char lengths written → assert `MaxCharLength` is null

**Expected Counterexamples**:
- Compilation failure: `LineIndex` has no `MaxByteLength` property
- If stubbed: cached value doesn't match iteration result

### Fix Checking

**Goal**: Verify cached properties always equal iteration-computed maximums.

**Pseudocode:**
```
FOR ALL sequences of AppendByteLengths calls DO
  result := lineIndex.MaxByteLength
  expected := max(all byte lengths across all appends)
  ASSERT result = expected
END FOR

FOR ALL sequences of SetCharLength calls DO
  result := lineIndex.MaxCharLength
  expected := max(all char lengths set so far)
  ASSERT result = expected
END FOR
```

### Preservation Checking

**Goal**: Verify existing `LineIndex` operations produce identical results after fix.

**Pseudocode:**
```
FOR ALL input WHERE NOT isBugCondition(input) DO
  ASSERT GetByteLength(i) unchanged
  ASSERT GetCharLength(i) unchanged
  ASSERT GetByteOffset(i) unchanged
  ASSERT LineCount unchanged
END FOR
```

**Testing Approach**: Property-based testing generates random sequences of appends and char-length writes, verifying all existing accessors return same values.

**Test Cases**:
1. Random byte lengths → `GetByteLength(i)` returns correct value for each line
2. Random char lengths → `GetCharLength(i)` returns correct value for written lines, null for unwritten
3. Random appends → `LineCount` equals total lines appended
4. `Clear()` → all fields reset to zero/empty

### Unit Tests

- `MaxByteLength` after single append
- `MaxByteLength` after multiple appends with increasing/decreasing values
- `MaxCharLength` null before any `SetCharLength`
- `MaxCharLength` after partial and full char-length writes
- `Clear()` resets both max fields

### Property-Based Tests

- Generate random `ulong[]` spans, append, assert `MaxByteLength == span.Max()`
- Generate random char lengths for subset of lines, assert `MaxCharLength == max(written)`
- Generate random operation sequences, verify `GetByteLength`/`GetCharLength` unchanged

### Integration Tests

- `HandleGetScrollInfo` returns correct max values using cached properties
- Response format unchanged: `scanState\nlineCount\nmaxByteLength\nmaxCharLength`
