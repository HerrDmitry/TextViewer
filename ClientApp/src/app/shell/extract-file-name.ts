/**
 * Extracts the file name (last path segment) from a full file path.
 * Handles both Windows backslash and Unix forward slash separators.
 */
export function extractFileName(filePath: string): string {
  const lastSep = Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\'));
  return lastSep === -1 ? filePath : filePath.substring(lastSep + 1);
}
