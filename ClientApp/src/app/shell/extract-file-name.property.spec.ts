/**
 * Feature: viewer-ui-shell, Property 2: File name extraction yields last path segment
 *
 * Validates: Requirements 3.1
 *
 * Property: For any valid file path string containing at least one path separator
 * (forward slash or backslash), extractFileName shall return the substring after
 * the last separator. For paths with no separator, it shall return the entire string.
 */
import * as fc from 'fast-check';
import { extractFileName } from './extract-file-name';

// Characters valid in path segments (no separators, no null)
const segmentChars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._- ';

/** Generator for a single path segment (no separators) */
const pathSegment = fc.string({
  minLength: 1,
  maxLength: 10,
  unit: fc.constantFrom(...segmentChars.split('')),
});

describe('Feature: viewer-ui-shell, Property 2: File name extraction yields last path segment', () => {
  it('returns substring after last separator for paths with separators', () => {
    const separator = fc.constantFrom('/', '\\');

    fc.assert(
      fc.property(
        fc.array(pathSegment, { minLength: 1, maxLength: 5 }),
        separator,
        pathSegment,
        (segments: string[], sep: string, fileName: string) => {
          const filePath = segments.join(sep) + sep + fileName;
          const result = extractFileName(filePath);
          return result === fileName;
        }
      ),
      { numRuns: 10 }
    );
  });

  it('returns the entire string for paths with no separator', () => {
    fc.assert(
      fc.property(pathSegment, (input: string) => {
        return extractFileName(input) === input;
      }),
      { numRuns: 10 }
    );
  });
});
