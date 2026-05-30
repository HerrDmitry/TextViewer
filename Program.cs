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

        messageBus.RegisterHandler("open-file", (correlationId, payload) =>
        {
            try
            {
                var files = app.MainWindow.ShowOpenFile("Open File", "", false, null);
                if (files != null && files.Length > 0 && !string.IsNullOrEmpty(files[0]))
                {
                    return Task.FromResult<string?>(files[0]);
                }
                return Task.FromResult<string?>("");
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"ERROR: {ex.GetType().Name}: {ex.Message}");
            }
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
}
