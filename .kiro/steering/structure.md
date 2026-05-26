# Project Structure

```
TextViewer/
├── Program.cs                  # .NET entry point — configures PhotinoBlazorAppBuilder, window props
├── App.razor                   # Blazor root component (mounted into #app in index.html)
├── TextViewer.csproj           # MSBuild project file (.NET 10, Photino.Blazor, esbuild target)
├── wwwroot/                    # Build output (generated — do not edit directly)
│   ├── index.html              # Host HTML page (source of truth, not generated)
│   ├── main.js                 # esbuild bundle output
│   └── styles.css              # Copied from ClientApp/src/styles.css
├── ClientApp/                  # Frontend source
│   ├── src/
│   │   ├── main.ts             # App entry point
│   │   ├── styles.css          # Global styles (copied to wwwroot/)
│   │   └── app/
│   │       ├── app.component.ts
│   │       └── app.component.html
│   ├── tsconfig.json           # TypeScript config (for type-checking only)
│   ├── angular.json            # Build config documentation
│   └── vendor/                 # Vendored dependencies (see vendor.md)
│       ├── esbuild/
│       │   └── esbuild.exe     # esbuild bundler binary (win-x64, ~10MB)
│       ├── @angular/           # Angular type stubs (for IDE/type-checking)
│       │   ├── core/
│       │   └── platform-browser/
│       └── typescript/         # TypeScript compiler (for type-checking only)
│           └── lib/
└── .kiro/
    ├── specs/
    └── steering/
```

## Conventions

- **Single-project structure**: .NET host + frontend source in one repo
- **wwwroot/ is generated**: esbuild output lands here. Only `index.html` is hand-authored (it's the host page).
- **MSBuild orchestrates frontend build**: `.csproj` `BuildAngular` target runs esbuild before compile
- **Program.cs uses class with Main**: `[STAThread] public static void Main(string[] args)` (required by Photino)
- **esbuild bundles to IIFE**: Output is `--format=iife` so no `type="module"` needed in HTML
- **Embedded resources**: All `wwwroot/**` embedded in assembly for single-file deployment
