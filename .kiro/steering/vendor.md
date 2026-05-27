# Frontend Dependencies — npm

All frontend tooling uses canonical Angular CLI via npm. No vendored binaries.

## Setup

```bash
cd ClientApp
npm ci
```

## Build

```bash
# Via MSBuild (automatic during dotnet build)
dotnet build

# Manually
cd ClientApp
npx ng build --configuration production
```

## Version Pinning — MANDATORY POLICY

**RULE: Every package MUST be pinned at least 3 minor versions behind the latest available release. Upgrading to latest (or within 2 minor of latest) is FORBIDDEN.**

Applies to ALL deps — Angular, RxJS, TypeScript, zone.js, everything.

| Dependency | Pinned | Latest at pin time | Gap |
|-----------|--------|-------------------|-----|
| Angular | 19.2.14 | 22.x | 3 minor |
| @angular/cli | 19.2.16 | 22.x | 3 minor |
| TypeScript | 5.7.3 | 5.8.x+ | ≥1 minor (constrained by Angular compat range) |
| RxJS | 7.8.1 | 7.8.x | patch-level (no newer minor exists yet) |
| zone.js | 0.15.0 | 0.15.x | patch-level (no newer minor exists yet) |

### Enforcement

- `save-exact=true` in `.npmrc` → no range specifiers ever
- `prefer-offline=true` → reduces accidental fetches of newer versions
- **CI script**: `npm run check-versions` — queries npm registry, fails if any dep within 3 minor of latest
- **Agent rule**: NEVER propose upgrading any package to within 2 minor of latest. Always verify gap before any version bump.
- **`npm update` is FORBIDDEN** — manual version selection only
- **`npm audit fix --force` is FORBIDDEN** — may bump major/minor
- **`ng update` is FORBIDDEN** — Angular's auto-upgrade tool bypasses pinning

### When a dep has no newer minor (e.g. RxJS 7.8.x is latest)

Pin to latest available. Policy applies once a new minor ships.

## Upgrade Procedure

1. Check latest version of target pkg on npm registry
2. Compute target = latest - 3 minor versions (minimum gap)
3. Verify target compatible w/ other pinned deps (esp Angular↔TypeScript matrix at https://angular.dev/reference/versions)
4. Update `package.json` exact version
5. `rm -rf node_modules package-lock.json && npm install`
6. `npx ng build` to verify

## .npmrc

```ini
save-exact=true        # no ^ or ~ ever
prefer-offline=true    # don't fetch newer unless explicit
update-notifier=false  # suppress "new version available" noise
fund=false             # suppress funding messages
audit=false            # suppress audit (use manual audit only)
```
