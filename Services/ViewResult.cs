namespace TextViewer.Services;

public sealed class ViewResult
{
    public IReadOnlyList<string> Rows { get; }

    public ViewResult(IReadOnlyList<string> rows)
    {
        Rows = rows;
    }
}
