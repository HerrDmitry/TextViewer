# Global Design

#[[file:.kiro/specs/_global/design-shared.md]]

## Overview

This document captures the full product design for all shipped features. Architecture, build pipeline, communication model, and error handling patterns are provided by design-shared.md. This document covers feature-specific component interfaces, state, correctness properties, and testing.

## Components and Interfaces

### 1. .NET Host Application (`Program.cs`)

Entry point — configures and launches Photino.Blazor app.

**Responsibilities:**
- Create and configure `PhotinoBlazorAppBuilder`
- Register Blazor root component
- Configure Photino window properties (title, size, resizability)
- Register `WebMessageReceived` handler for message routing
- Start application event loop

**Interface:**
```csharp
var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);
appBuilder.RootComponents.Add<App>("app");

var app = appBuilder.Build();

app.MainWindow
    .SetTitle("Text Viewer")
    .SetUseOsDefaultSize(true)
    .SetResizable(true);

// Message routing
app.MainWindow.RegisterWebMessageReceivedHandler((sender, message) =>
{
    if (message == "open-file")
    {
        // Show native file dialog
        // SendWebMessage(path) or SendWebMessage("")
    }
});

app.Run();
```

### 2. Blazor Root Component (`App.razor`)

Minimal Blazor component — mounting point for Angular app.

**Interface:**
```razor
@* App.razor *@
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8" />
    <base href="/" />
    <link rel="stylesheet" href="styles.css" />
</head>
<body>
    <app-root></app-root>
    <script src="polyfills.js"></script>
    <script src="main.js"></script>
</body>
</html>
```

### 3. Angular `AppComponent`

```typescript
@Component({ standalone: true, selector: 'app-root' })
export class AppComponent {
  displayText = signal('Hello World');
  private awaitingResponse = signal(false);

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void;

  private onMessageReceived(message: string): void;
}
```

| Method | Trigger | Effect |
|--------|---------|--------|
| `onKeydown` | `document:keydown` | If Ctrl+O/Cmd+O and not awaiting → `preventDefault`, send `"open-file"`, set awaiting |
| `onMessageReceived` | Photino bridge message | If non-empty → set `displayText`; clear awaiting |

### 4. Project File (`TextViewer.csproj`)

**Publish Configuration:**
```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <RuntimeIdentifiers>win-x64;osx-x64;osx-arm64;linux-x64</RuntimeIdentifiers>
</PropertyGroup>
```

**Embedded Resources:**
```xml
<ItemGroup>
  <EmbeddedResource Include="wwwroot\**\*" />
  <Content Remove="wwwroot\**\*" />
</ItemGroup>
```

## Data Models

### Frontend State

```typescript
interface AppState {
  displayText: string;       // Current text in Display_Area ("Hello World" initially)
  awaitingResponse: boolean; // Guards against duplicate dialog requests
}
```

### Window Configuration

| Property | Type | Default Value |
|----------|------|---------------|
| Title | string | "Text Viewer" |
| UseOsDefaultSize | bool | true |
| Resizable | bool | true |

### Message Protocol

| Direction | Format | Example |
|-----------|--------|---------|
| JS → .NET | Plain string command | `"open-file"` |
| .NET → JS | Plain string (result or empty) | `"C:\Users\me\docs\report.pdf"` or `""` |

## Correctness Properties

### Property 1: State guard prevents duplicate sends

*For any* sequence of Ctrl+O key presses and message responses interleaved in any order, the frontend shall never have more than one outstanding "open-file" message without an intervening response.

**Validates: Requirements 3.2, 3.4**

## Testing Strategy

### Property-Based Tests

**Library**: fast-check (TypeScript PBT)

**Config**: Minimum 100 iterations per property.

| Property | Generates | Asserts |
|----------|-----------|---------|
| State guard (Property 1) | Random sequences of `{keypress, response}` events | At most 1 outstanding send at any time |

### Unit Tests

| Test | Validates |
|------|-----------|
| Ctrl+O triggers `sendMessage("open-file")` | Req 3.1 |
| Other key combos don't trigger send | Req 3.1 |
| `preventDefault` called on Ctrl+O in both states | Req 3.3 |
| Cmd+O works (meta key) | Req 3.1 |
| Initial `displayText` is "Hello World" | Req 6.1, 6.2 |
| Non-empty response sets displayText | Req 5.1 |
| Empty response leaves display unchanged | Req 5.2 |
| Backend sends path on dialog confirm | Req 4.3 |
| Backend sends "" on dialog cancel | Req 4.4 |
| Backend ignores non-"open-file" messages | Req 4.5 |
| Angular `AppComponent` renders "Hello World" | Req 2.1 |
| Window title is "Text Viewer" | Req 1.1 |

### Integration Tests

| Test | Validates |
|------|-----------|
| App launches without exceptions | Req 1 |
| Ctrl+O → dialog → path displayed | Req 3.1, 4.1, 4.3, 5.1 |
| Ctrl+O → dialog cancel → display unchanged | Req 4.4, 5.2 |
| Published binary runs w/o external files | shared: deploy model |

### Test Boundaries

- Frontend unit/property tests: mock `window.external.sendMessage`, simulate incoming messages
- Backend integration tests: mock native dialog API, verify `SendWebMessage` calls
- No E2E browser automation — Photino bridge tested via integration
