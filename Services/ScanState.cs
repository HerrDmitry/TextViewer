namespace TextViewer.Services;

public enum ScanState
{
    NotStarted = 0,
    QuickScanInProgress = 1,
    QuickScanComplete = 2,
    FullScanInProgress = 3,
    FullScanComplete = 4,
    Failed = 5,
    Cancelled = 6
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
