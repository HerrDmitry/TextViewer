using Photino.NET;

namespace TextViewer.Services;

/// <summary>
/// Adapts PhotinoWindow to IMessageBridge for use with MessageBusHost.
/// </summary>
public sealed class PhotinoMessageBridge : IMessageBridge
{
    private readonly PhotinoWindow _window;

    public PhotinoMessageBridge(PhotinoWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.RegisterWebMessageReceivedHandler(OnWebMessageReceived);
    }

    public void SendWebMessage(string message) => _window.SendWebMessage(message);

    public event EventHandler<string>? WebMessageReceived;

    private void OnWebMessageReceived(object? sender, string message)
    {
        WebMessageReceived?.Invoke(this, message);
    }
}
