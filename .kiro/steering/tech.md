# Tech Stack

## Runtime & Language
- **C# / .NET 10** — Backend host and application entry point
- **TypeScript** — Frontend UI (compiled via Angular CLI)

## Key Libraries & Frameworks
- **Photino.Blazor 4.0.13** — Bridges .NET and the native OS webview; hosts web content in a desktop window
- **Photino.NET** — Underlying native window abstraction (WebView2 on Windows, WebKit on macOS/Linux)
- **Blazor** — Serves static assets to the webview from embedded resources via `ManifestEmbeddedFileProvider`
- **Angular 19.2** — Frontend framework (standalone components, signals)

## Build System
- **MSBuild / dotnet CLI** — .NET project build and publish
- **Angular CLI (`ng build`)** — Compiles & bundles Angular app → `wwwroot/`
- **npm** — Package manager for frontend deps
- **Node.js** — Required for Angular CLI build

## Project SDK
- `Microsoft.NET.Sdk.Razor`

## Dependency Management
- Frontend deps in `ClientApp/package.json` (exact pinned versions)
- `npm ci` for reproducible installs
- `.npmrc` enforces `save-exact=true`

## Common Commands

```bash
# Build entire project (MSBuild target invokes ng build automatically)
dotnet build

# Run the application
dotnet run

# Install frontend deps
cd ClientApp && npm ci

# Build Angular manually
cd ClientApp && npx ng build

# Publish for a specific platform
dotnet publish -r win-x64
dotnet publish -r osx-arm64
dotnet publish -r linux-x64
```

## Testing

| Layer | Tool | Command |
|-------|------|---------|
| .NET integration | xUnit | `dotnet test` |
| Build verification | CI pipeline | `dotnet build` + asset checks |

## Embedded Resource Strategy
- `wwwroot/` files embedded via `<EmbeddedResource Include="wwwroot\**" />` + `<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>`
- At runtime: `ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot")` serves files from assembly
- Photino.Blazor 4.x resolves `IFileProvider` from DI for static file serving
- Single-file publish: all assets inside the exe, no external files needed
