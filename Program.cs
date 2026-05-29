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

        messageBus.RegisterHandler("open-file", async (correlationId, payload) =>
        {
            try
            {
                var files = app.MainWindow.ShowOpenFile("Open File", "", false, null);
                if (files != null && files.Length > 0 && !string.IsNullOrEmpty(files[0]))
                {
                    var filePath = files[0];

                    // Quick scan → find longest line (byte width)
                    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
                    var logger = loggerFactory.CreateLogger<FileIndex>();
                    using var fileIndex = new FileIndex(filePath, CancellationToken.None, logger);
                    await fileIndex.StartScanAsync();

                    if (fileIndex.State == ScanState.FullScanComplete)
                    {
                        ulong maxCharLen = 0;
                        for (int i = 0; i < fileIndex.Index.LineCount; i++)
                        {
                            var cl = fileIndex.Index.GetCharLength(i);
                            if (cl.HasValue && cl.Value > maxCharLen)
                                maxCharLen = cl.Value;
                        }
                        return $"{filePath} | {fileIndex.Index.LineCount} lines | longest: {maxCharLen} chars";
                    }

                    return $"{filePath} | scan failed: {fileIndex.Error}";
                }
                return "";
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.GetType().Name}: {ex.Message}";
            }
        });

        AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
        {
            app.MainWindow.ShowMessage("Fatal Exception", error.ExceptionObject.ToString());
        };

        app.Run();
    }
}
