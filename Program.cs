using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;

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

        app.MainWindow.RegisterWebMessageReceivedHandler((object? sender, string message) =>
        {
            if (message == "open-file")
            {
                try
                {
                    var files = app.MainWindow.ShowOpenFile("Open File", "", false, null);
                    if (files != null && files.Length > 0 && !string.IsNullOrEmpty(files[0]))
                    {
                        app.MainWindow.SendWebMessage(files[0]);
                    }
                    else
                    {
                        app.MainWindow.SendWebMessage("");
                    }
                }
                catch
                {
                    app.MainWindow.SendWebMessage("");
                }
            }
            // Any other message is silently ignored
        });

        AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
        {
            app.MainWindow.ShowMessage("Fatal Exception", error.ExceptionObject.ToString());
        };

        app.Run();
    }
}
