# Shared Requirements Context

## Application Identity

- **Name**: Text Viewer
- **Type**: Cross-platform desktop application for viewing text content

## Technology Stack

- **Runtime**: C# / .NET 10
- **Desktop framework**: Photino.Blazor 4.x (native OS webview host)
- **Frontend framework**: Angular 19.2 (standalone components, signals)
- **Frontend language**: TypeScript
- **Build system**: MSBuild + Angular CLI (`ng build`)
- **Package manager**: npm (exact pinned versions)

## Platform Targets

- Windows (WebView2)
- macOS (WebKit)
- Linux (WebKit)

## Deployment Model

- Single-file self-contained executable per platform RID
- All Angular assets embedded as .NET embedded resources
- No external files, folders, or installed runtimes required at runtime
- Publish flags: `PublishSingleFile` + `SelfContained` + `IncludeAllContentForSelfExtract` + `EnableCompressionInSingleFile`

## Communication Model

- Bidirectional string-based message bridge between Angular frontend and .NET backend
- JS → .NET: `window.external.sendMessage(string)`
- .NET → JS: `PhotinoWindow.SendWebMessage(string)`
- Each feature defines its own command vocabulary
- No JSON serialization unless payload complexity warrants it

## Cross-Platform Constraints

- Keyboard shortcuts: Ctrl on Windows/Linux, Cmd on macOS for equivalent actions
- Native OS dialogs for file system interactions
- Platform-native webview rendering (no Electron/Chromium bundling)

## Glossary (Shared Terms)

- **Application**: The TextViewer desktop application
- **Photino_Window**: The native OS window hosting web content
- **Blazor_Host**: The Blazor host serving Angular content within Photino_Window
- **Angular_Frontend**: The Angular application rendering the UI
- **Message_Bridge**: Photino bidirectional communication channel
- **Published_Executable**: Single-file self-contained binary from `dotnet publish`
