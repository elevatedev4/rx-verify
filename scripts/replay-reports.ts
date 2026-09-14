#!/usr/bin/env tsx
/**
 * Replays each HQ field-error report through the CURRENT engine
 * comparators, to check whether the engine now produces the status Will
 * (the pharmacy owner) says is correct for that field.
 *
 * Reads .reports.json at runtime (git-excluded — see .git/info/exclude in
 * the repo root; never commit that file or paste its values into source).
 * This script itself embeds NO report values — it's a generic harness.
 *
 * Usage: npx tsx scripts/replay-reports.ts [path-to-reports.json]
 */

import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { compareAddresses } from '../src/normalize/address.js';
import { compareDrugs, FixtureProvider } from '../src/drug/index.js';
import { compareSigs } from '../src/sig/index.js';
import { compareRefills, comparePrescriberPhone, comparePrescriberNpi } from '../src/quantity/index.js';
import type { Address } from '../src/types.js';

interface Report {
  id: string;
  field: string;
  source: string;
  entered: string;
  status: string; // status the app showed
  reasonCode?: string;
  explanation?: string;
  correction?: string; // Will's words = expected verdict
  engineBuild?: string;
  commit?: string;
  sourceInputMode?: string;
  refillsTotalFillsLabelSeen?: string;
  refillsTotalFillsLabelPrefix?: string;
  logTail?: string;
  reportStatus?: string;
}

const FIXED_BUILDS = new Set(['19c63b8', 'dab046e', '75b662a']);

function isBeforeFixedBuilds(commit: string | undefined): boolean {
  // We can't diff commit ancestry cheaply here without a git call per
  // report; the three named commits are call-out landmarks in the brief.
  // Treat any report whose engineBuild/commit prefix does not match one
  // of the three fixed commits (or a descendant we can't easily prove)
  // as "unknown / not confirmed post-fix" -- printed for the operator to
  // eyeball against `git log --oneline` rather than guessed here.
  if (!commit) return false;
  return !FIXED_BUILDS.has(commit.slice(0, 7));
}

/** Very loose freeform-address-line splitter so replay can build an Address from a single-line report value shaped like "street, city, ST zip" or "street unit city, ST zip". Falls back to putting the whole raw string in `street` when nothing recognizable is found -- compareAddresses' own parseFreeformAddress does the real parsing internally when only `street` is populated, so this just needs to hand it the raw line. */
function addressFromFreeform(raw: string | null | undefined): Address | null {
  if (!raw || !raw.trim()) return null;
  return { street: raw.trim() };
}

function runAddress(r: Report): { status: string; reasonCode: string; explanation: string } {
  const src = addressFromFreeform(r.source);
  const ent = addressFromFreeform(r.entered);
  return compareAddresses(src, ent);
}

const drugProvider = new FixtureProvider();
function runDrug(r: Report): { status: string; reasonCode: string; explanation: string } {
  return compareDrugs({ name: r.source }, { name: r.entered }, drugProvider);
}

function runSig(r: Report): { status: string; reasonCode: string; explanation: string } {
  return compareSigs(r.source, r.entered);
}

function runRefills(r: Report): { status: string; reasonCode: string; explanation: string } {
  // sourceIsTotalFills is inferred here only for display -- the real
  // extraction fix lives in src/ocr/parseEscriptOcr.ts; this harness
  // just reflects whatever the report's own fields tell us about how the
  // source value was read (refillsTotalFillsLabelSeen/-Prefix).
  const sourceIsTotalFills = Boolean(
    r.refillsTotalFillsLabelSeen && /total\s*fills/i.test(r.refillsTotalFillsLabelSeen)
  );
  return compareRefills(r.source, r.entered, sourceIsTotalFills);
}

function runPrescriberPhone(r: Report): { status: string; reasonCode: string; explanation: string } {
  const src = r.source === '(not provided)' ? null : r.source;
  return comparePrescriberPhone(src, r.entered);
}

// prescriberNpi's single "new" report is a UI-spacing note (see brief),
// not a comparator defect -- comparePrescriberNpi is imported and left
// available/tested elsewhere, but deliberately has no RUNNERS entry so
// this harness prints the UI-note branch below instead of a bogus verdict.
void comparePrescriberNpi;

const RUNNERS: Record<string, (r: Report) => { status: string; reasonCode: string; explanation: string }> = {
  prescriberAddress: runAddress,
  drug: runDrug,
  sig: runSig,
  refills: runRefills,
  prescriberPhone: runPrescriberPhone
};

function main() {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const argPath = process.argv[2];
  const reportsPath = argPath ?? path.join(here, '..', '.reports.json');
  if (!existsSync(reportsPath)) {
    console.error(`No reports file at ${reportsPath}. Pass a path or place .reports.json at repo root.`);
    process.exit(1);
  }
  const data = JSON.parse(readFileSync(reportsPath, 'utf8')) as { reports: Report[] };
  const targets = data.reports.filter((r) => r.reportStatus === 'new');

  console.log(`${targets.length} reports with reportStatus "new"\n`);
  console.log(
    ['id', 'field', 'appStatus', 'engineStatus/reason', 'willCorrection', 'preFixed?'].join(' | ')
  );
  console.log('-'.repeat(120));

  for (const r of targets) {
    const runner = RUNNERS[r.field];
    let engineOut = 'NO RUNNER FOR FIELD';
    if (runner) {
      try {
        const result = runner(r);
        engineOut = `${result.status}/${result.reasonCode}`;
      } catch (e) {
        engineOut = `THREW: ${(e as Error).message}`;
      }
    } else if (r.field === 'prescriberNpi') {
      engineOut = '(UI-only report — see overlay MainWindow.xaml spacing)';
    }
    const preFix = isBeforeFixedBuilds(r.commit) ? 'pre-fix build' : 'post named fixes';
    console.log(
      [r.id, r.field, r.status, engineOut, r.correction ?? '', preFix].join(' | ')
    );
  }
}

main();
