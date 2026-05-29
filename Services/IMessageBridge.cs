namespace TextViewer.Services;

/// <summary>
/// Abstraction over the Photino window's message bridge for testability.
/// Wraps SendWebMessage and WebMessageReceived operations.
/// </summary>
public interface IMessageBridge
{
    /// <summary>
    /// Sends a string message to the frontend via the webview bridge.
    /// </summary>
    void SendWebMessage(string message);

    /// <summary>
    /// Raised when a message is received from the frontend via the webview bridge.
    /// </summary>
    event EventHandler<string>? WebMessageReceived;
}
