namespace TextViewer.Services;

/// <summary>
/// Result of a wrapped-mode view extraction.
/// Contains the raw content string and per-visual-row line number annotations.
/// </summary>
public sealed class WrappedViewResult
{
    /// <summary>The extracted content (with newline delimiters preserved).</summary>
    public string Content { get; }

    /// <summary>
    /// One entry per visual row (col-count-based split).
    /// First visual row of each logical line → 1-based line number.
    /// Continuation rows → null.
    /// </summary>
    public IReadOnlyList<int?> LineNumbers { get; }

    public WrappedViewResult(string content, IReadOnlyList<int?> lineNumbers)
    {
        Content = content;
        LineNumbers = lineNumbers;
    }
}
