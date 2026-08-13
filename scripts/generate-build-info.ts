/**
 * Prebuild step — automatically run by `npm run build` via npm's
 * lifecycle hook convention (any script named "prebuild" runs
 * immediately before "build", no wiring needed beyond the name; see
 * package.json). Writes dist/build-info.json: { sha, builtAt } —
 * the exact source commit and build time that produced whatever else
 * ends up in dist/.
 *
 * WHY (RXVERIFY-TROUBLESHOOT 2026-08-13): a live troubleshoot report
 * showed the OCR Total-Fills refills bug still reproducing at
 * "Commit: 94d4cce4" (the app's own embedded git sha — see
 * AppDiagnostics.GetCommitSha() in the C# overlay), yet a from-scratch
 * repro against the checked-out TypeScript source at that exact commit
 * came back green — the parser was already correct. The overlay's C#
 * commit sha only describes the CHECKOUT; it says nothing about
 * whether `dist/cli.js` — the compiled file the overlay's EngineClient
 * actually spawns via `node dist/cli.js --serve` — was rebuilt AFTER
 * that commit landed. This stamp closes that gap: --serve now reports
 * it once in its ready handshake (see src/cli.ts), EngineClient.cs
 * captures it, and RxLogFormatter prints it in every "Copy logs (no
 * HIPAA)" header next to App version/Commit — so the NEXT troubleshoot
 * report can distinguish "the fix landed but dist/ is stale, rebuild
 * it" from "this is a genuine new regression" at a glance.
 *
 * sha: `git rev-parse --short HEAD`, falling back to "unknown" if git
 * isn't available (e.g. a zip-exported checkout with no .git directory)
 * or the command fails for any other reason — this must NEVER fail the
 * build; a missing/failed git command is a normal, tolerated case, not
 * an error worth surfacing.
 * builtAt: an ISO-8601 UTC timestamp taken at the moment this script
 * runs.
 *
 * Usage (automatic): `npm run build` runs this first.
 * Usage (manual): `npx tsx scripts/generate-build-info.ts`
 */

import { execSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';

interface BuildInfo {
  sha: string;
  builtAt: string;
}

function getShortSha(): string {
  try {
    const sha = execSync('git rev-parse --short HEAD', {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore']
    }).trim();
    return sha || 'unknown';
  } catch {
    return 'unknown';
  }
}

const buildInfo: BuildInfo = {
  sha: getShortSha(),
  builtAt: new Date().toISOString()
};

// dist/ may not exist yet on a from-scratch build (prebuild runs BEFORE
// tsc creates it) — create it here rather than relying on build order.
mkdirSync('dist', { recursive: true });
writeFileSync('dist/build-info.json', JSON.stringify(buildInfo, null, 2) + '\n', 'utf8');

process.stdout.write(`dist/build-info.json written: ${JSON.stringify(buildInfo)}\n`);
