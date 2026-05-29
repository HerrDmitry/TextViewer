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
