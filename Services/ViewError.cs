namespace TextViewer.Services;

public enum ViewErrorCode
{
    InvalidParameter,
    FileNotAccessible,
    IoError,
    Cancelled
}

public sealed class ViewError
{
    public ViewErrorCode Code { get; }
    public string Message { get; }

    public ViewError(ViewErrorCode code, string message)
    {
        Code = code;
        Message = message;
    }
}
