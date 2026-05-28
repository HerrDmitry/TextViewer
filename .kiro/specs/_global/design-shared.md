# Shared Design Context

## Architecture Layers

```mermaid
graph TD
    A[Program.cs - Entry Point] --> B[PhotinoBlazorAppBuilder]
    B --> C[Blazor Host]
    C --> D[Photino Window]
    D --> E[WebView - Platform Native]
    E --> F[Angular Application]

    subgraph ".NET 10 Process"
        A
        B
        C
    end

    subgraph "Native OS Window"
        D
        E
    end

    subgraph "Web Content"
        F
    end
```

- **Native layer**: Photino → OS-native window w/ platform webview
- **Host layer**: Photino.Blazor → bridges .NET and webview, serves static content, routes messages
- **Frontend layer**: Angular → compiled to static assets, served through Blazor host

## Communication Model

```mermaid
sequenceDiagram
    participant Angular as Angular Frontend
    participant Bridge as Photino Message Bridge
    participant DotNet as .NET Backend

    Angular->>Bridge: window.external.sendMessage(command)
    Bridge->>DotNet: WebMessageReceived handler
    DotNet->>DotNet: Process command
    DotNet->>Bridge: SendWebMessage(response)
    Bridge->>Angular: message event
```

- String-based protocol, no JSON unless needed
- Each feature defines command strings
- Bridge is pass-through — no logic

## Build Pipeline

```mermaid
graph LR
    A[Angular Source] -->|ng build| B[Compiled Assets]
    B -->|Output to wwwroot/| C[.NET Project wwwroot/]
    C -->|EmbeddedResource glob| D[Assets embedded in assembly]
    D -->|dotnet publish| E[Single-File Binary]
```

- MSBuild `BuildAngular` target runs `npx ng build` before compile
- Angular output → `wwwroot/` → embedded in assembly
- `ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot")` serves at runtime

## Project Layout (Key Files)

| File | Role |
|------|------|
| `Program.cs` | Entry point, window config, message handler registration |
| `App.razor` | Blazor root component, HTML shell loading Angular |
| `TextViewer.csproj` | MSBuild orchestration, publish config, embedded resources |
| `ClientApp/src/app/app.component.ts` | Angular root component |
| `ClientApp/src/app/app.component.html` | Angular root template |
| `ClientApp/angular.json` | Angular CLI build config → outputs to `wwwroot/` |

## Error Handling Patterns

| Category | Strategy |
|----------|----------|
| Missing embedded assets | Provider returns not-found → blank page; build validation prevents |
| WebView unavailable | Photino throws `PlatformNotSupportedException` → app exits |
| Unknown message from frontend | Backend ignores silently |
| Native operation failure | Catch, send empty/error response → graceful degradation |
| Build-time failures | MSBuild target fails → blocks build |

## Design Conventions

- **Fail-fast on startup**: No recovery for infrastructure failures
- **Signal-based state**: Angular signals for reactive UI
- **Guard patterns**: Prevent duplicate operations via boolean flags
- **No persistent storage**: State in component signals, resets on restart (until file content features)
- **Single project structure**: .NET host + Angular source in one repo
