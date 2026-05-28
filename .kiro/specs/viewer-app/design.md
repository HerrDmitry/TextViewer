# Design Document

#[[file:.kiro/specs/_global/design-shared.md]]

## Overview

Initial stub proving Photino.Blazor + Angular integration. Renders "Hello World" message, establishes foundational project structure.

## Components and Interfaces

### Angular `AppComponent`

Root component — displays static "Hello World" text.

```typescript
@Component({ standalone: true, selector: 'app-root' })
export class AppComponent {
  displayText = signal('Hello World');
}
```

Template:
```html
<p>{{ displayText() }}</p>
```

### .NET Host (`Program.cs`)

Configures window properties only (no message handling in this feature).

```csharp
var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);
appBuilder.RootComponents.Add<App>("app");

var app = appBuilder.Build();

app.MainWindow
    .SetTitle("Text Viewer")
    .SetUseOsDefaultSize(true)
    .SetResizable(true);

app.Run();
```

## Data Models

### Angular Component State

| Component | State | Value |
|-----------|-------|-------|
| AppComponent | displayText | "Hello World" |

### Window Configuration

| Property | Type | Default Value |
|----------|------|---------------|
| Title | string | "Text Viewer" |
| UseOsDefaultSize | bool | true |
| Resizable | bool | true |

## Correctness Properties

### Property 1: PBT not applicable

This feature tests infrastructure wiring, static configuration, and UI rendering — no algorithmic logic with meaningful input variation.

## Error Handling

| Scenario | Strategy |
|----------|----------|
| Missing Angular assets | Provider returns not-found → blank page; build validation prevents |
| WebView unavailable | Photino throws `PlatformNotSupportedException` → app exits |

## Testing Strategy

**Smoke Tests:**
- Application launches without exceptions
- Photino window opens
- Angular assets present in build output

**Unit Tests:**
- `AppComponent` renders "Hello World" text
- `AppComponent` is root component

**Build Verification:**
- `dotnet build` succeeds
- Angular output contains `main.js`, `polyfills.js`
- `dotnet publish -r win-x64` produces single executable
