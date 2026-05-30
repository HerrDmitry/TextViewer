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
            HandleCloseFile(payload, sessions, sessionLock);
            return Task.FromResult<string?>(null); // fire-and-forget
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
    /// Handles "get-view" message: parses payload, looks up session, calls GetViewAsync.
    /// Extracted for testability.
    /// </summary>
    internal static async Task<string?> HandleGetView(
        string payload,
        Dictionary<string, FileViewService> sessions,
        object sessionLock)
    {
        // Parse payload: viewSessionId\nstartLine\nstartCol\nrowCount\ncolCount
        var fields = payload.Split('\n');
        if (fields.Length != 5)
            return "ERROR:Invalid payload structure: expected 5 fields";

        var viewSessionId = fields[0];

        if (!int.TryParse(fields[1], out var startLine) || startLine < 0)
            return "ERROR:Invalid field: startLine";
        if (!int.TryParse(fields[2], out var startCol) || startCol < 0)
            return "ERROR:Invalid field: startCol";
        if (!int.TryParse(fields[3], out var rowCount) || rowCount < 1)
            return "ERROR:Invalid field: rowCount";
        if (!int.TryParse(fields[4], out var colCount) || colCount < 1)
            return "ERROR:Invalid field: colCount";

        FileViewService? service;
        lock (sessionLock)
        {
            sessions.TryGetValue(viewSessionId, out service);
        }

        if (service is null)
            return $"ERROR:Session not found: {viewSessionId}";

        var result = await service.GetViewAsync(startLine, startCol, rowCount, colCount);

        if (!result.IsSuccess)
            return $"ERROR:{result.Error.Message}";

        // Strip line-ending delimiters from each row, join with \n
        var rows = result.Value.Rows;
        var stripped = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            stripped[i] = StripDelimiter(rows[i]);
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
        object sessionLock)
    {
        var viewSessionId = payload;
        lock (sessionLock)
        {
            if (sessions.Remove(viewSessionId, out var service))
            {
                service.Dispose();
            }
        }
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
