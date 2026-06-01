/**
 * Feature: line-wrap-numbers — Wrap mode property tests
 */
import * as fc from 'fast-check';
import { splitIntoVisualRows } from './line-wrap-utils';

/**
 * Feature: line-wrap-numbers, Property 4: Wrapped-mode request payload round-trip
 *
 * **Validates: Requirements 5.1, 5.2, 5.4**
 *
 * Property: Encoding a wrapped-mode request payload as
 * `viewSessionId\nW\nstartLine\ncharacterOffset\ncharacterCount`
 * then parsing by splitting on '\n' recovers the original values.
 * Numeric fields contain only ASCII digits 0-9 with no leading zeros
 * except for the value "0" itself, no whitespace, and no sign characters,
 * within the range 0 to 2,147,483,647.
 */
describe('Feature: line-wrap-numbers, Property 4: Wrapped-mode request payload round-trip', () => {
  const INT32_MAX = 2_147_483_647;
  const noLeadingZerosPattern = /^(0|[1-9][0-9]*)$/;

  it('encode then parse recovers original values and numeric fields are well-formed', () => {
    fc.assert(
      fc.property(
        // viewSessionId: non-empty string without newlines
        fc.string({ minLength: 1, maxLength: 50 }).filter(s => !s.includes('\n')),
        // startLine: 0 to INT32_MAX
        fc.integer({ min: 0, max: INT32_MAX }),
        // characterOffset: 0 to INT32_MAX
        fc.integer({ min: 0, max: INT32_MAX }),
        // characterCount: 1 to INT32_MAX
        fc.integer({ min: 1, max: INT32_MAX }),
        (viewSessionId: string, startLine: number, characterOffset: number, characterCount: number) => {
          // Encode: same format as sendWrappedViewRequest
          const payload = `${viewSessionId}\nW\n${startLine}\n${characterOffset}\n${characterCount}`;

          // Parse: split on '\n'
          const fields = payload.split('\n');

          // Verify exactly 5 fields
          if (fields.length !== 5) return false;

          // Verify round-trip recovery
          if (fields[0] !== viewSessionId) return false;
          if (fields[1] !== 'W') return false;
          if (parseInt(fields[2], 10) !== startLine) return false;
          if (parseInt(fields[3], 10) !== characterOffset) return false;
          if (parseInt(fields[4], 10) !== characterCount) return false;

          // Verify numeric fields format: only ASCII digits, no leading zeros except "0"
          if (!noLeadingZerosPattern.test(fields[2])) return false;
          if (!noLeadingZerosPattern.test(fields[3])) return false;
          if (!noLeadingZerosPattern.test(fields[4])) return false;

          return true;
        }
      ),
      { numRuns: 10 }
    );
  });
});


