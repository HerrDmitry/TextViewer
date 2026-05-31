namespace TextViewer.Services;

public sealed class ViewResult
{
    public IReadOnlyList<string> Rows { get; }
    public IReadOnlyList<int> LineNumbers { get; }

    public ViewResult(IReadOnlyList<string> rows, IReadOnlyList<int> lineNumbers)
    {
        Rows = rows;
        LineNumbers = lineNumbers;
    }

    /// <summary>
    /// Legacy constructor — generates sequential line numbers starting at 1.
    /// </summary>
    public ViewResult(IReadOnlyList<string> rows)
    {
        Rows = rows;
        LineNumbers = Enumerable.Range(1, rows.Count).ToList();
    }
}
