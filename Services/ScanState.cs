namespace TextViewer.Services;

public enum ScanState
{
    NotStarted = 0,
    ScanInProgress = 1,
    ScanComplete = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>Reason a scan failed or was cancelled.</summary>
public enum ScanErrorCode
{
    FileNotFound,
    AccessDenied,
    IoError,
    OutOfMemory,
    Cancelled,
    Unknown
}

/// <summary>Structured scan failure info.</summary>
public sealed record ScanError(ScanErrorCode Code, string Message);

/// <summary>Summary of a successful scan completion.</summary>
public sealed record ScanSummary(int LineCount, System.Text.Encoding Encoding, int BomByteLength);
