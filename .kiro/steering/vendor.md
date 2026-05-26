# Vendor Dependencies — Download & Setup

All frontend tooling is vendored (no npm). If `ClientApp/vendor/` is missing or incomplete, follow these steps to restore it.

## Required Vendored Items

| Item | Path | Source | Purpose |
|------|------|--------|---------|
| esbuild (win-x64) | `ClientApp/vendor/esbuild/esbuild.exe` | npm registry tgz | Bundles TS → JS |
| esbuild (osx-arm64) | `ClientApp/vendor/esbuild/esbuild` | npm registry tgz | macOS build |
| esbuild (linux-x64) | `ClientApp/vendor/esbuild/esbuild` | npm registry tgz | Linux build |
| TypeScript compiler | `ClientApp/vendor/typescript/lib/` | npm registry tgz | Type-checking only (optional) |
| Angular type stubs | `ClientApp/vendor/@angular/` | Hand-written | IDE autocomplete + type-checking |

## Download Instructions

### esbuild (REQUIRED for build)

esbuild is a native binary — download the platform-specific package.

**Windows x64:**
```powershell
$url = "https://registry.npmjs.org/@esbuild/win32-x64/-/win32-x64-0.25.4.tgz"
$outDir = "ClientApp/vendor/esbuild"
$tgz = "$env:TEMP/esbuild.tgz"
New-Item -ItemType Directory -Force $outDir | Out-Null
Invoke-WebRequest -Uri $url -OutFile $tgz
tar -xzf $tgz -C $outDir
Move-Item "$outDir/package/esbuild.exe" "$outDir/esbuild.exe" -Force
Remove-Item "$outDir/package" -Recurse -Force
Remove-Item $tgz -Force
```

**macOS arm64:**
```bash
url="https://registry.npmjs.org/@esbuild/darwin-arm64/-/darwin-arm64-0.25.4.tgz"
mkdir -p ClientApp/vendor/esbuild
curl -L "$url" | tar -xz -C ClientApp/vendor/esbuild
mv ClientApp/vendor/esbuild/package/bin/esbuild ClientApp/vendor/esbuild/esbuild
chmod +x ClientApp/vendor/esbuild/esbuild
rm -rf ClientApp/vendor/esbuild/package
```

**macOS x64:**
```bash
url="https://registry.npmjs.org/@esbuild/darwin-x64/-/darwin-x64-0.25.4.tgz"
# Same steps as arm64 above
```

**Linux x64:**
```bash
url="https://registry.npmjs.org/@esbuild/linux-x64/-/linux-x64-0.25.4.tgz"
mkdir -p ClientApp/vendor/esbuild
curl -L "$url" | tar -xz -C ClientApp/vendor/esbuild
mv ClientApp/vendor/esbuild/package/bin/esbuild ClientApp/vendor/esbuild/esbuild
chmod +x ClientApp/vendor/esbuild/esbuild
rm -rf ClientApp/vendor/esbuild/package
```

### TypeScript Compiler (OPTIONAL — type-checking only)

Only needed for `tsc --noEmit` type-checking. Not required for build (esbuild handles bundling).

```powershell
$url = "https://registry.npmjs.org/typescript/-/typescript-5.8.3.tgz"
$outDir = "ClientApp/vendor/typescript"
$tgz = "$env:TEMP/typescript.tgz"
New-Item -ItemType Directory -Force $outDir | Out-Null
Invoke-WebRequest -Uri $url -OutFile $tgz
tar -xzf $tgz -C $outDir
Move-Item "$outDir/package/lib" "$outDir/lib" -Force
Remove-Item "$outDir/package" -Recurse -Force
Remove-Item $tgz -Force
```

### Angular Type Stubs (OPTIONAL — IDE support)

These are hand-written minimal type declarations. Not downloaded from npm. They live at:
- `ClientApp/vendor/@angular/core/index.d.ts`
- `ClientApp/vendor/@angular/platform-browser/index.d.ts`

If missing, create them with the Angular decorator interfaces (`Component`, `NgModule`, `Injectable`, `bootstrapApplication`, etc.). They only need type signatures, not runtime code.

## Verification

After vendoring, verify build works:
```bash
dotnet build
```

This runs the `BuildAngular` MSBuild target which invokes:
```
ClientApp\vendor\esbuild\esbuild.exe ClientApp/src/main.ts --bundle --outfile=wwwroot/main.js --format=iife --target=es2022
```

## Version Pinning

| Dependency | Version | Update procedure |
|-----------|---------|-----------------|
| esbuild | 0.25.4 | Download new tgz from `https://registry.npmjs.org/@esbuild/{platform}/-/{platform}-{version}.tgz` |
| TypeScript | 5.8.3 | Download new tgz from `https://registry.npmjs.org/typescript/-/typescript-{version}.tgz` |

## Cross-Platform Build Note

The `.csproj` `BuildAngular` target references `ClientApp\vendor\esbuild\esbuild.exe` (Windows path). For cross-platform CI, the target should detect OS and use the correct binary. Current setup is Windows-only for local dev.
