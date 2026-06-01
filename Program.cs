using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Photino.Blazor;
using TextViewer.Services;

namespace TextViewer;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        // Serve wwwroot from embedded resources so no physical directory is needed.
        builder.Services.AddSingleton<IFileProvider>(
            new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot"));

        // Register the Blazor root component (App.razor renders the host HTML).
        builder.RootComponents.Add<App>("#app");

        var app = builder.Build();

        app.MainWindow
            .SetTitle("Text Viewer")
            .SetUseOsDefaultSize(true)
            .SetResizable(true);

        // Set up Message Bus
        var bridge = new PhotinoMessageBridge(app.MainWindow);
        var messageBus = new MessageBusHost(bridge);

        // Session storage
        var appLifetimeCts = new CancellationTokenSource();
        var sessions = new Dictionary<string, FileViewService>();
        var sessionLock = new object(); // guards dictionary access from handler + scan-monitor
        var wrappedLineCountCache = new Dictionary<string, (int colCount, int lineCount, long total)>();

        messageBus.RegisterHandler("open-file", async (correlationId, payload) =>
        {
            try
            {
                var files = app.MainWindow.ShowOpenFile("Open File", "", false, null);
                if (files != null && files.Length > 0 && !string.IsNullOrEmpty(files[0]))
                {
                    var filePath = files[0];
                    var viewSessionId = Guid.NewGuid().ToString();

                    // Parse viewport dimensions from payload
                    var (rowCount, colCount) = ParseOpenFilePayload(payload);

                    // Create FileViewService (starts scan automatically)
                    var logger = app.Services.GetRequiredService<ILogger<FileViewService>>();
                    var service = new FileViewService(filePath, appLifetimeCts.Token, logger);

                    lock (sessionLock)
                    {
                        sessions[viewSessionId] = service;
                    }

                    // Start scan-complete monitor
                    _ = MonitorScanState(service, viewSessionId, messageBus);

                    // Wait 500ms for scan to make initial progress
                    await Task.Delay(500);

                    // Get initial view rows
                    try
                    {
                        var result = await service.GetViewAsync(0, 0, rowCount, colCount);
                        return FormatOpenFileResponse(viewSessionId, filePath, result);
                    }
                    catch
                    {
                        // If GetViewAsync fails, return empty Initial_View (zero rows)
                        return $"{viewSessionId}\n{filePath}";
                    }
                }
                return "";
            }
            catch (Exception ex)
            {
                return $"ERROR:{ex.GetType().Name}: {ex.Message}";
            }
        });

        messageBus.RegisterHandler("get-view", async (correlationId, payload) =>
        {
            return await HandleGetView(payload, sessions, sessionLock);
        });

        messageBus.RegisterHandler("close-file", (correlationId, payload) =>
        {
            HandleCloseFile(payload, sessions, sessionLock, wrappedLineCountCache);
            return Task.FromResult<string?>(null); // fire-and-forget
        });

        messageBus.RegisterHandler("get-scroll-info", (correlationId, payload) =>
        {
            return Task.FromResult<string?>(HandleGetScrollInfo(payload, sessions, sessionLock));
        });

        messageBus.RegisterHandler("get-wrapped-line-count", (correlationId, payload) =>
        {
            return Task.FromResult<string?>(HandleGetWrappedLineCount(payload, sessions, sessionLock, wrappedLineCountCache));
        });

        messageBus.RegisterHandler("exit", (correlationId, payload) =>
        {
            app.MainWindow.Close();
            return Task.FromResult<string?>(null);
        });

        AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
        {
            app.MainWindow.ShowMessage("Fatal Exception", error.ExceptionObject.ToString());
        };

        app.Run();
    }

    internal static string StripDelimiter(string row)
    {
        if (row.Length == 0) return row;
        if (row.EndsWith("\r\n")) return row[..^2];
        if (row[^1] == '\n' || row[^1] == '\r') return row[..^1];
        return row;
    }

    internal static async Task MonitorScanState(FileViewService service, string viewSessionId, MessageBusHost messageBus)
    {
        while (true)
        {
            await Task.Delay(50); // poll interval

            var state = service.ScanState;

            if (state >= ScanState.FullScanComplete)
            {
                messageBus.Send("scan-complete", viewSessionId);
                break;
            }

            if (state == ScanState.Failed)
                break;
        }
    }

    /// <summary>
    /// Handles "get-view" message: parses payload, looks up session, calls GetViewAsync or GetWrappedViewAsync.
    /// Extracted for testability.
    /// </summary>
    internal static async Task<string?> HandleGetView(
        string payload,
        Dictionary<string, FileViewService> sessions,
        object sessionLock)
    {
        var fields = payload.Split('\n');

        // Detect wrapped mode: second field is "W"
        // Accepts 5 fields (legacy) or 6 fields (with colCount)
        if ((fields.Length == 5 || fields.Length == 6) && fields[1] == "W")
        {
            // Wrapped-mode request: viewSessionId\nW\nstartLine\ncharOffset\ncharCount[\ncolCount]
            var viewSessionId = fields[0];

            if (!int.TryParse(fields[2], out var startLine) || startLine < 0)
                return "ERROR: startLine out of range";
            if (!int.TryParse(fields[3], out var charOffset) || charOffset < 0)
                return "ERROR: characterOffset out of range";
            if (!int.TryParse(fields[4], out var charCount) || charCount < 1)
                return "ERROR: characterCount out of range";

            int wrappedColCount = 1;
            if (fields.Length == 6)
            {
                if (!int.TryParse(fields[5], out wrappedColCount) || wrappedColCount < 1)
                    return "ERROR: colCount out of range";
            }

            FileViewService? service;
            lock (sessionLock) { sessions.TryGetValue(viewSessionId, out service); }
            if (service is null)
                return "ERROR: Session not found";

            // Resolve visual row index to (startLine, characterOffset)
            var lineIndex = service.LineIndex;
            var lineCount = lineIndex.LineCount;
            var resolved = ResolveVisualRowIndex(lineIndex, lineCount, wrappedColCount, startLine);

            var result = await service.GetWrappedViewAsync(
                resolved.startLine, resolved.characterOffset, charCount, wrappedColCount);
            if (!result.IsSuccess)
                return result.Error.Message;

            // Format response: L:{n1},{n2},{n3},...\n{content}
            var wrappedLineNumbers = result.Value.LineNumbers;
            var header = "L:" + string.Join(",",
                wrappedLineNumbers.Select(n => n.HasValue ? n.Value.ToString() : ""));
            return header + "\n" + result.Value.Content;
        }

        // Standard rectangular mode (existing logic)
        if (fields.Length != 5)
            return "ERROR:Invalid payload structure: expected 5 fields";

        var rectViewSessionId = fields[0];

        if (!int.TryParse(fields[1], out var rectStartLine) || rectStartLine < 0)
            return "ERROR:Invalid field: startLine";
        if (!int.TryParse(fields[2], out var startCol) || startCol < 0)
            return "ERROR:Invalid field: startCol";
        if (!int.TryParse(fields[3], out var rowCount) || rowCount < 1)
            return "ERROR:Invalid field: rowCount";
        if (!int.TryParse(fields[4], out var colCount) || colCount < 1)
            return "ERROR:Invalid field: colCount";

        FileViewService? rectService;
        lock (sessionLock)
        {
            sessions.TryGetValue(rectViewSessionId, out rectService);
        }

        if (rectService is null)
            return $"ERROR:Session not found: {rectViewSessionId}";

        var rectResult = await rectService.GetViewAsync(rectStartLine, startCol, rowCount, colCount);

        if (!rectResult.IsSuccess)
            return $"ERROR:{rectResult.Error.Message}";

        // Strip line-ending delimiters from each row, prefix with line number + tab, join with \n
        var rows = rectResult.Value.Rows;
        var lineNumbers = rectResult.Value.LineNumbers;
        var stripped = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            stripped[i] = $"{lineNumbers[i]}\t{StripDelimiter(rows[i])}";
        }
        return string.Join('\n', stripped);
    }

    /// <summary>
    /// Handles "close-file" message: disposes and removes session.
    /// Extracted for testability.
    /// </summary>
    internal static void HandleCloseFile(
        string payload,
        Dictionary<string, FileViewService> sessions,
        object sessionLock,
        Dictionary<string, (int colCount, int lineCount, long total)> wrappedLineCountCache)
    {
        var viewSessionId = payload;
        lock (sessionLock)
        {
            if (sessions.Remove(viewSessionId, out var service))
            {
                service.Dispose();
            }
        }
        wrappedLineCountCache.Remove(viewSessionId);
    }

    /// <summary>
    /// Handles "get-scroll-info" message: looks up session, reads ScanState and LineIndex,
    /// computes max byte_length and max char_length, returns scanState\nlineCount\nmaxByteLength\nmaxCharLength.
    /// Extracted for testability.
    /// </summary>
    internal static string HandleGetScrollInfo(
        string payload,
        Dictionary<string, FileViewService> sessions,
        object sessionLock)
    {
        var viewSessionId = payload;

        FileViewService? service;
        lock (sessionLock)
        {
            sessions.TryGetValue(viewSessionId, out service);
        }

        if (service is null)
            return $"ERROR:Session not found: {viewSessionId}";

        var scanState = service.ScanState;
        var lineIndex = service.LineIndex;
        var lineCount = lineIndex.LineCount;

        // O(1) cached max values from LineIndex
        ulong maxByteLength = lineIndex.MaxByteLength;
        ulong maxCharLength = lineIndex.MaxCharLength ?? 0;

        // Response: scanState\nlineCount\nmaxByteLength\nmaxCharLength
        return $"{scanState}\n{lineCount}\n{maxByteLength}\n{maxCharLength}";
    }



    /// <summary>
    /// Handles "get-wrapped-line-count" message: parses payload, validates session and colCount,
    /// checks cache, computes if needed, returns total as string.
    /// </summary>
    internal static string HandleGetWrappedLineCount(
        string payload,
        Dictionary<string, FileViewService> sessions,
        object sessionLock,
        Dictionary<string, (int colCount, int lineCount, long total)> wrappedLineCountCache)
    {
        var newlineIdx = payload.IndexOf('\n');
        if (newlineIdx == -1) return "ERROR: Invalid payload";

        var sessionId = payload[..newlineIdx];

        FileViewService? service;
        lock (sessionLock) { sessions.TryGetValue(sessionId, out service); }
        if (service is null) return $"ERROR: Session not found: {sessionId}";

        if (!int.TryParse(payload[(newlineIdx + 1)..], out var colCount) || colCount < 1)
            return "ERROR: colCount must be >= 1";

        var lineIndex = service.LineIndex;
        var lineCount = lineIndex.LineCount;

        // Cache check
        if (wrappedLineCountCache.TryGetValue(sessionId, out var cached)
            && cached.colCount == colCount && cached.lineCount == lineCount)
        {
            return cached.total.ToString();
        }

        // Compute and cache
        long total = ComputeWrappedLineCount(lineIndex, lineCount, colCount);
        wrappedLineCountCache[sessionId] = (colCount, lineCount, total);
        return total.ToString();
    }

    /// <summary>
    /// Computes total visual row count across all lines using parallel iteration.
    /// Each line: if charLen is null, fall back to byte length; if length == 0 → 1 visual row;
    /// else ceil(len / colCount).
    /// </summary>
    internal static long ComputeWrappedLineCount(LineIndex lineIndex, int lineCount, int colCount)
    {
        if (lineCount == 0) return 0;

        long total = 0;
        Parallel.For(0, lineCount, () => 0L, (i, _, subtotal) =>
        {
            var charLen = lineIndex.GetCharLength(i);
            long len = (long)(charLen ?? lineIndex.GetByteLength(i));
            subtotal += len == 0 ? 1 : (len + colCount - 1) / colCount;
            return subtotal;
        },
        subtotal => Interlocked.Add(ref total, subtotal));

        return total;
    }

    /// <summary>
    /// Parses viewport dimensions from open-file payload.
    /// Returns (rowCount, colCount) with fallback to (40, 120).
    /// Extracted for testability.
    /// </summary>
    internal static (int rowCount, int colCount) ParseOpenFilePayload(string? payload)
    {
        int rowCount = 40;
        int colCount = 120;
        if (!string.IsNullOrEmpty(payload))
        {
            var dims = payload.Split('\n');
            if (dims.Length >= 2)
            {
                if (int.TryParse(dims[0], out var r) && r >= 1) rowCount = r;
                if (int.TryParse(dims[1], out var c) && c >= 1) colCount = c;
            }
        }
        return (rowCount, colCount);
    }

    /// <summary>
    /// Resolves a zero-based visual row index to (startLine, characterOffset).
    /// Iterates lines summing visual rows until cumulative sum exceeds target.
    /// Clamps to last visual row when index exceeds total; returns (0, 0) when lineCount == 0 or visualRowIndex == 0.
    /// </summary>
    internal static (int startLine, int characterOffset) ResolveVisualRowIndex(
        LineIndex lineIndex, int lineCount, int colCount, long visualRowIndex)
    {
        if (lineCount == 0 || visualRowIndex == 0)
            return (0, 0);

        long cumulative = 0;
        for (int i = 0; i < lineCount; i++)
        {
            var charLen = lineIndex.GetCharLength(i);
            long len = (long)(charLen ?? lineIndex.GetByteLength(i));
            long visualRows = len == 0 ? 1 : (len + colCount - 1) / colCount;

            if (cumulative + visualRows > visualRowIndex)
            {
                long rowWithinLine = visualRowIndex - cumulative;
                int characterOffset = (int)(rowWithinLine * colCount);
                return (i, characterOffset);
            }
            cumulative += visualRows;
        }

        // Clamp to last visual row
        var lastCharLen = lineIndex.GetCharLength(lineCount - 1);
        long lastLineLen = (long)(lastCharLen ?? lineIndex.GetByteLength(lineCount - 1));
        long lastVisualRows = lastLineLen == 0 ? 1 : (lastLineLen + colCount - 1) / colCount;
        int lastOffset = (int)((lastVisualRows - 1) * colCount);
        return (lineCount - 1, lastOffset);
    }

    /// <summary>
    /// Formats the open-file response: viewSessionId\nfilePath\nrow1\nrow2\n...
    /// Extracted for testability.
    /// </summary>
    internal static string FormatOpenFileResponse(string viewSessionId, string filePath, Result<ViewResult, ViewError> viewResult)
    {
        var initialRows = "";
        if (viewResult.IsSuccess && viewResult.Value.Rows.Count > 0)
        {
            var stripped = new string[viewResult.Value.Rows.Count];
            for (int i = 0; i < viewResult.Value.Rows.Count; i++)
            {
                stripped[i] = StripDelimiter(viewResult.Value.Rows[i]);
            }
            initialRows = "\n" + string.Join('\n', stripped);
        }
        return $"{viewSessionId}\n{filePath}{initialRows}";
    }
}
