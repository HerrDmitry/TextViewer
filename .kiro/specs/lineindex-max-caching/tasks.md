# Implementation Plan

- [x] 1. Write bug condition exploration test
  - **Property 1: Bug Condition** - MaxByteLength Equals Iteration Maximum
  - **CRITICAL**: This test MUST FAIL on unfixed code - failure confirms the bug exists
  - **DO NOT attempt to fix the test or the code when it fails**
  - **NOTE**: This test encodes the expected behavior - it will validate the fix when it passes after implementation
  - **GOAL**: Surface counterexamples that demonstrate `LineIndex` lacks cached max properties
  - **Scoped PBT Approach**: Generate random `ulong[]` byte-length spans (1-50 elements, values 1-100000), append to `LineIndex`, assert `MaxByteLength == span.Max()`. Generate random char lengths for lines 0..N-1, assert `MaxCharLength == max(written char lengths)`. When no char lengths written, assert `MaxCharLength == null`.
  - Test file: `TextViewer.Tests/Services/LineIndexMaxCachingBugConditionTests.cs`
  - Use FsCheck with `[Property(MaxTest = 10)]`
  - On UNFIXED code: test will not compile (properties don't exist) — confirms bug
  - **EXPECTED OUTCOME**: Compilation failure or test failure proves bug exists
  - Document counterexamples found
  - Mark task complete when test is written, run, and failure is documented
  - _Requirements: 1.1, 1.2, 2.1, 2.2, 2.3_

- [x] 2. Write preservation property tests (BEFORE implementing fix)
  - **Property 2: Preservation** - Existing LineIndex Behavior Unchanged
  - **IMPORTANT**: Follow observation-first methodology
  - Observe: `AppendByteLengths` + `GetByteLength(i)` returns correct values on unfixed code
  - Observe: `SetCharLength` + `GetCharLength(i)` returns correct values on unfixed code
  - Observe: `LineCount` equals total appended lines on unfixed code
  - Write property-based tests: generate random `ulong[]` spans (1-50 elements), append, verify `GetByteLength(i)` matches input for each i. Generate random char lengths, set them, verify `GetCharLength(i)` matches. Verify `LineCount` correct.
  - Test file: `TextViewer.Tests/Services/LineIndexMaxCachingPreservationTests.cs`
  - Use FsCheck with `[Property(MaxTest = 10)]`
  - Run tests on UNFIXED code
  - **EXPECTED OUTCOME**: Tests PASS (confirms baseline behavior to preserve)
  - Mark task complete when tests are written, run, and passing on unfixed code
  - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5_

- [x] 3. Implement max caching fix

  - [x] 3.1 Add cached fields and properties to `LineIndex`
    - Add `private volatile ulong _maxByteLength` field
    - Add `private volatile ulong _maxCharLength` field
    - Add `public ulong MaxByteLength => _maxByteLength` property
    - Add `public ulong? MaxCharLength => _charLengthsWrittenUpTo == 0 ? null : _maxCharLength` property
    - _Bug_Condition: isBugCondition(request) where request.type = "get-scroll-info" AND lineCount > 0_
    - _Expected_Behavior: MaxByteLength = max(all byte lengths), MaxCharLength = max(all char lengths) or null_
    - _Preservation: All existing LineIndex read/write operations unchanged_
    - _Requirements: 2.1, 2.2, 2.3_

  - [x] 3.2 Update `AppendByteLengths` to track `_maxByteLength`
    - Inside existing `_writeLock`, after `_segments.Append(...)`, iterate `byteLengths` span
    - Compare each value with `_maxByteLength`, update if larger
    - Volatile write ensures visibility to concurrent readers
    - _Requirements: 2.1, 3.1_

  - [x] 3.3 Update `SetCharLength` to track `_maxCharLength`
    - After writing char length to segment, compare `charLength` with `_maxCharLength`
    - Update `_maxCharLength` if `charLength` is larger (volatile write)
    - Must happen before existing `_charLengthsWrittenUpTo` volatile write (ordering preserved)
    - _Requirements: 2.2, 3.2_

  - [x] 3.4 Update `Clear()` to reset max fields
    - Add `_maxByteLength = 0` and `_maxCharLength = 0` inside existing lock
    - _Requirements: 3.5_

  - [x] 3.5 Update `HandleGetScrollInfo` in `Program.cs` to use cached properties
    - Replace O(N) iteration loop with: `var maxByteLength = lineIndex.MaxByteLength;` and `var maxCharLength = lineIndex.MaxCharLength ?? 0;`
    - Remove the `for` loop computing max values
    - Response format unchanged: `scanState\nlineCount\nmaxByteLength\nmaxCharLength`
    - _Requirements: 2.1, 2.2, 3.6_

  - [x] 3.6 Verify bug condition exploration test now passes
    - **Property 1: Expected Behavior** - MaxByteLength Equals Iteration Maximum
    - **IMPORTANT**: Re-run the SAME test from task 1 - do NOT write a new test
    - The test from task 1 encodes the expected behavior
    - When this test passes, it confirms the expected behavior is satisfied
    - Run bug condition exploration test from step 1
    - **EXPECTED OUTCOME**: Test PASSES (confirms bug is fixed)
    - _Requirements: 2.1, 2.2, 2.3_

  - [x] 3.7 Verify preservation tests still pass
    - **Property 2: Preservation** - Existing LineIndex Behavior Unchanged
    - **IMPORTANT**: Re-run the SAME tests from task 2 - do NOT write new tests
    - Run preservation property tests from step 2
    - **EXPECTED OUTCOME**: Tests PASS (confirms no regressions)
    - Confirm all tests still pass after fix (no regressions)

- [x] 4. Checkpoint - Ensure all tests pass
  - Run `dotnet test` to verify all existing + new tests pass
  - Ensure no regressions in existing `LineIndexTests`
  - Ask the user if questions arise
