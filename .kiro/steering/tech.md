# Tech Stack

## Runtime & Language
- **C# / .NET 10** — Backend host and application entry point
- **TypeScript** — Frontend UI (bundled via esbuild)

## Key Libraries & Frameworks
- **Photino.Blazor 4.0.13** — Bridges .NET and the native OS webview; hosts web content in a desktop window
- **Photino.NET** — Underlying native window abstraction (WebView2 on Windows, WebKit on macOS/Linux)
- **Blazor** — Serves static assets to the webview from embedded resources via `ManifestEmbeddedFileProvider`

## Build System
- **MSBuild / dotnet CLI** — .NET project build and publish
- **esbuild** (vendored binary) — Bundles TypeScript → single JS file; resolves imports, no npm needed
- **No npm / npx** — Forbidden. All dependencies downloaded manually and vendored.
- **Node.js** — NOT required for build (esbuild is native binary). Only needed if tsc type-checking is desired.

## Project SDK
- `Microsoft.NET.Sdk.Razor`

## Dependency Management
- All frontend deps vendored into `ClientApp/vendor/`
- No `package.json` install step; deps already present in tree
- esbuild binary is platform-specific (win-x64 currently)

## Common Commands

```bash
# Build entire .NET project (MSBuild target invokes esbuild automatically)
dotnet build

# Run the application
dotnet run

# Publish for a specific platform
dotnet publish -r win-x64
dotnet publish -r osx-arm64
dotnet publish -r linux-x64

# Bundle TypeScript manually (esbuild)
ClientApp\vendor\esbuild\esbuild.exe ClientApp/src/main.ts --bundle --outfile=wwwroot/main.js --format=iife --target=es2022
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
