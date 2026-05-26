# Implementation Plan: Viewer App — Hello World Stub

## Overview

Establish the foundational cross-platform desktop application using C# .NET 10 with Photino.Blazor hosting an Angular frontend. The application publishes as a single self-contained executable with all Angular assets embedded as resources. Implementation builds from the .NET project file with publish configuration, through the embedded file provider, host application, Angular frontend, and MSBuild integration, ending with single-file publish verification.

## Tasks

- [x] 1. Set up .NET project with publish and embedded resource configuration
  - [x] 1.1 Create the .NET project file (`TextViewer.csproj`)
    - Use `Microsoft.NET.Sdk.Razor` SDK targeting `net10.0`
    - Add `Photino.Blazor` NuGet package reference
    - Configure output type as `WinExe`
    - Add publish properties: `PublishSingleFile=true`, `SelfContained=true`, `IncludeAllContentForSelfExtract=true`, `EnableCompressionInSingleFile=true`
    - Add `RuntimeIdentifiers`: `win-x64;osx-x64;osx-arm64;linux-x64`
    - Add `EmbeddedResource Include="wwwroot\**\*"` glob
    - Add `Content Remove="wwwroot\**\*"` to avoid duplication in publish output
    - _Requirements: 1.2, 3.1, 6.1, 6.2, 6.3_

- [x] 2. Implement EmbeddedStaticFileProvider
  - [x] 2.1 Create `EmbeddedStaticFileProvider.cs`
    - Implement `IFileProvider` interface
    - Constructor accepts `Assembly` and `string baseNamespace` parameters
    - `GetFileInfo(string subpath)`: convert path separators to resource name format (replace `/` and `\` with `.`), prepend base namespace, call `Assembly.GetManifestResourceStream`
    - Return a custom `EmbeddedResourceFileInfo` when stream exists, or `NotFoundFileInfo` when null
    - `GetDirectoryContents`: return `NotFoundDirectoryContents.Singleton`
    - `Watch`: return `NullChangeToken.Singleton`
    - _Requirements: 6.2, 6.4_

  - [x] 2.2 Create `EmbeddedResourceFileInfo.cs`
    - Implement `IFileInfo` wrapping a `ManifestResourceStream`
    - Properties: `Exists=true`, `IsDirectory=false`, `Length` from stream, `Name` from resource path
    - `CreateReadStream()` returns the embedded resource stream
    - _Requirements: 6.2_

- [x] 3. Create application entry point and Blazor root component
  - [x] 3.1 Create `Program.cs`
    - Use top-level statements
    - Create `PhotinoBlazorAppBuilder`, register root component `App` at selector `"app"`
    - Instantiate `EmbeddedStaticFileProvider` with `typeof(Program).Assembly` and base namespace `"TextViewer.wwwroot"`
    - Register `IFileProvider` in DI: `appBuilder.Services.AddSingleton<IFileProvider>(fileProvider)`
    - Configure window: title "Text Viewer", `SetUseOsDefaultSize(true)`, `SetResizable(true)`
    - Call `app.Run()` to start the event loop
    - _Requirements: 1.1, 3.1, 3.2, 5.1, 5.2, 5.3, 6.2_

  - [x] 3.2 Create `App.razor`
    - HTML shell with `<!DOCTYPE html>`, charset meta, `<base href="/" />`
    - Reference Angular output files: `styles.css`, `runtime.js`, `polyfills.js`, `main.js`
    - Include `<app-root></app-root>` element as Angular mount point
    - _Requirements: 3.1, 3.2, 4.1_

- [x] 4. Checkpoint — Verify .NET host compiles
  - Ensure `dotnet build` succeeds for the .NET project (Angular assets not yet present, but host code compiles).
  - Ask the user if questions arise.

- [x] 5. Set up Angular frontend application
  - [x] 5.1 Create Angular project structure (`ClientApp/`)
    - Create `ClientApp/tsconfig.json` with Angular compiler options
    - Create `ClientApp/src/main.ts` — Angular bootstrap entry using `bootstrapApplication`
    - Vendor Angular framework files (no npm/npx — download and commit dependencies)
    - _Requirements: 4.1, 4.2_

  - [x] 5.2 Implement the root AppComponent
    - Create `ClientApp/src/app/app.component.ts` with selector `app-root`
    - Create `ClientApp/src/app/app.component.html` rendering "Hello World" text
    - This is the Hello_World_View and the default view on startup
    - _Requirements: 2.1, 2.2, 4.2_

  - [x] 5.3 Configure Angular build output
    - Create `ClientApp/angular.json` (or equivalent tsconfig/build config) with output path targeting `../wwwroot/`
    - Ensure compiled output produces `main.js`, `polyfills.js`, `runtime.js`, `styles.css`
    - _Requirements: 4.3_

- [x] 6. Integrate Angular build into MSBuild pipeline
  - [x] 6.1 Add MSBuild targets to `TextViewer.csproj` for TypeScript compilation
    - Add a pre-build target that invokes `node tsc.js --project ClientApp/tsconfig.json`
    - Ensure Angular assets land in `wwwroot/` before .NET build completes
    - Validate that `dotnet build` produces the full application with Angular assets embedded
    - _Requirements: 4.3, 3.2, 6.2_

  - [x] 6.2 Verify end-to-end build produces expected output
    - Run `dotnet build` and confirm `wwwroot/` contains `main.js`, `polyfills.js`, `runtime.js`, `styles.css`
    - Confirm embedded resources are included in the compiled assembly
    - _Requirements: 4.3, 1.1, 6.2_

- [x] 7. Verify single-file publish produces self-contained binary
  - [x] 7.1 Run `dotnet publish -r win-x64` and verify output
    - Confirm publish output contains exactly one executable file (no loose DLLs or folders alongside it)
    - Confirm no `wwwroot/` folder exists next to the published binary
    - Confirm binary size is reasonable (contains .NET runtime + Photino native libs + embedded Angular assets)
    - _Requirements: 6.1, 6.3, 6.4_

- [x] 8. Final checkpoint — Full build, publish, and launch verification
  - Ensure `dotnet build` succeeds with all Angular assets present.
  - Ensure `dotnet publish -r win-x64` produces a single self-contained executable.
  - Ensure `dotnet run` launches the Photino window displaying "Hello World".
  - Ask the user if questions arise.

## Notes

- No property-based tests are included — the design explicitly identifies this feature as configuration/integration work without algorithmic logic suitable for PBT.
- All frontend dependencies are vendored (no npm/npx per project conventions). The AI or a script downloads Angular framework files directly.
- MSBuild orchestrates the TypeScript build via `node tsc.js`, not `ng build` or npm scripts.
- The `wwwroot/` folder is generated output — never hand-edit files there.
- Each task references specific requirements for traceability.
- Checkpoints ensure incremental validation.
- The `EmbeddedStaticFileProvider` serves Angular assets from embedded resources at runtime, enabling the single-file deployment model.
- Publish configuration uses `IncludeAllContentForSelfExtract=true` to ensure native libraries (Photino native, WebView2 loader) are also packed into the single file.

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["2.1", "5.1"] },
    { "id": 2, "tasks": ["2.2", "5.2", "5.3"] },
    { "id": 3, "tasks": ["3.1", "3.2"] },
    { "id": 4, "tasks": ["6.1"] },
    { "id": 5, "tasks": ["6.2"] },
    { "id": 6, "tasks": ["7.1"] }
  ]
}
```
