#!/usr/bin/env node
/**
 * Version Policy Checker
 *
 * Verifies all dependencies in package.json are pinned at least 3 minor
 * versions behind the latest available on npm registry.
 *
 * Usage: node scripts/check-version-policy.mjs [--min-gap 3]
 *
 * Exit codes:
 *   0 = all deps compliant
 *   1 = one or more deps too close to latest
 */

import { readFileSync } from 'fs';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const MIN_GAP = parseInt(process.argv.find((_, i, a) => a[i - 1] === '--min-gap') ?? '3', 10);

const pkgPath = resolve(__dirname, '..', 'package.json');
const pkg = JSON.parse(readFileSync(pkgPath, 'utf8'));

const allDeps = {
  ...pkg.dependencies,
  ...pkg.devDependencies,
};

/**
 * Packages exempt from the 3-minor-behind rule.
 * Reason: these packages don't release enough minor versions to comply,
 * or are tightly coupled to Angular's version and have no independent cadence.
 * Each entry: package name → reason string.
 */
const EXEMPT = {
  'rxjs': 'Releases infrequently; latest 7.8.x has no 3-minor-old alternative compatible with Angular 19',
  'tslib': 'Helper library with no meaningful minor cadence; always use version matching TypeScript',
  'zone.js': 'Tied to Angular release cycle; no independent minor cadence',
};

/**
 * Parse semver string → {major, minor, patch}
 */
function parseSemver(version) {
  const clean = version.replace(/^[~^>=<\s]+/, '');
  const [major, minor, patch] = clean.split('.').map(Number);
  return { major, minor: minor ?? 0, patch: patch ?? 0 };
}

/**
 * Fetch latest version from npm registry
 */
async function fetchLatest(pkgName) {
  const url = `https://registry.npmjs.org/${pkgName}/latest`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`Failed to fetch ${pkgName}: ${res.status}`);
  const data = await res.json();
  return data.version;
}

/**
 * Compute minor version gap.
 * For same major: gap = latestMinor - pinnedMinor
 * For different major: treat as large gap (compliant)
 */
function minorGap(pinned, latest) {
  if (pinned.major < latest.major) {
    // Behind by major version(s) → definitely compliant
    return Infinity;
  }
  if (pinned.major > latest.major) {
    // Ahead of latest? Shouldn't happen, flag it
    return -1;
  }
  // Same major
  return latest.minor - pinned.minor;
}

/**
 * Check if a package has enough minor versions to comply.
 * If latest minor < MIN_GAP, there's no version 3 minors behind — exempt.
 */
function canComply(latest) {
  // If latest minor is less than MIN_GAP, impossible to be 3 behind
  return latest.minor >= MIN_GAP;
}

async function main() {
  console.log(`Version policy check: min gap = ${MIN_GAP} minor versions\n`);

  const results = [];
  const errors = [];

  for (const [name, pinnedVersion] of Object.entries(allDeps)) {
    try {
      const latestVersion = await fetchLatest(name);
      const pinned = parseSemver(pinnedVersion);
      const latest = parseSemver(latestVersion);
      const gap = minorGap(pinned, latest);

      const status =
        gap >= MIN_GAP || gap === Infinity
          ? 'OK'
          : name in EXEMPT
            ? 'EXEMPT'
            : !canComply(latest)
              ? 'EXEMPT'
              : 'FAIL';
      results.push({ name, pinnedVersion, latestVersion, gap, status });
    } catch (err) {
      errors.push({ name, error: err.message });
    }
  }

  // Print results
  const maxName = Math.max(...results.map(r => r.name.length), 10);
  console.log(
    'Package'.padEnd(maxName + 2) +
    'Pinned'.padEnd(12) +
    'Latest'.padEnd(12) +
    'Gap'.padEnd(6) +
    'Status'
  );
  console.log('-'.repeat(maxName + 44));

  for (const r of results) {
    const gapStr = r.gap === Infinity ? '∞' : String(r.gap);
    console.log(
      r.name.padEnd(maxName + 2) +
      r.pinnedVersion.padEnd(12) +
      r.latestVersion.padEnd(12) +
      gapStr.padEnd(6) +
      r.status
    );
  }

  if (errors.length > 0) {
    console.log('\nErrors:');
    for (const e of errors) {
      console.log(`  ${e.name}: ${e.error}`);
    }
  }

  const failures = results.filter(r => r.status === 'FAIL');
  if (failures.length > 0) {
    console.log(`\n❌ ${failures.length} package(s) within ${MIN_GAP} minor versions of latest.`);
    console.log('   Upgrade is FORBIDDEN. Downgrade or wait for latest to advance.');
    process.exit(1);
  } else {
    console.log(`\n✓ All packages compliant (≥${MIN_GAP} minor behind latest).`);
    process.exit(0);
  }
}

main();
