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

- Bidirectional message bridge between Angular frontend and .NET backend
- All communication routed through **Message Bus** layer — direct raw bridge calls from application code prohibited
- Raw transport: `window.external.sendMessage` (JS→.NET), `PhotinoWindow.SendWebMessage` (.NET→JS)
- Full protocol, queuing, routing, and error specs: see `requirements-bus-service.md` / `design-bus-service.md`

## Cross-Platform Constraints

- Keyboard shortcuts: Ctrl on Windows/Linux, Cmd on macOS for equivalent actions
- Native OS dialogs for file system interactions
- Platform-native webview rendering (no Electron/Chromium bundling)

## Glossary (Shared Terms)

- **Application**: The TextViewer desktop application
- **Photino_Window**: The native OS window hosting web content
- **Blazor_Host**: The Blazor host serving Angular content within Photino_Window
- **Angular_Frontend**: The Angular application rendering the UI
- **Message_Bridge**: Photino bidirectional communication channel (raw transport layer)
- **Message_Bus**: Application-level communication service on top of Message_Bridge (see `requirements-bus-service.md`)
- **Message_Bus_Client**: Angular singleton — outbound queuing, inbound routing
- **Message_Bus_Host**: .NET service — handler dispatch, response encoding
- **Published_Executable**: Single-file self-contained binary from `dotnet publish`
