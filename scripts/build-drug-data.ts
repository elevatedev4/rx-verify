/**
 * BUILD-TIME ONLY script. Run manually by a maintainer when refreshing
 * the local drug dataset — never invoked by the app at runtime.
 *
 * Downloads the public openFDA NDC directory bulk file, transforms it
 * into a compact local JSON file keyed by normalized 11-digit package
 * NDC, and writes it to `data/ndc-data.json`. That file is the ONLY
 * thing the app reads at runtime (see src/drug/index.ts,
 * LocalNdcProvider) — this script's network access never touches the
 * verify path.
 *
 * Usage:
 *   npx tsx scripts/build-drug-data.ts
 *
 * Requires `unzip` on PATH (present by default on macOS/Linux). Needs
 * network access; nothing else in this repo does.
 *
 * Source: https://open.fda.gov/apis/drug/ndc/ (public domain FDA data,
 * no account needed). This is drug reference data (product/ingredient/
 * strength/form), never patient data — it doesn't fall under the
 * "SYNTHETIC DATA ONLY" rule in the README, which is about
 * patient/prescriber PHI, not public FDA drug catalogs.
 */

import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { parseNdc } from '../src/drug/index.js';
import type { LocalConcept, LocalDrugData } from '../src/drug/local-data-format.js';

const SOURCE_URL = 'https://download.open.fda.gov/drug/ndc/drug-ndc-0001-of-0001.json.zip';
// Committed artifact is gzip-compressed (~6MB vs ~34MB raw) — see
// LocalNdcProvider in src/drug/index.ts for the runtime read side.
const OUTPUT_PATH = path.join(import.meta.dirname, '..', 'data', 'ndc-data.json.gz');

interface OpenFdaActiveIngredient {
  name: string;
  strength: string;
}

interface OpenFdaPackaging {
  package_ndc: string;
}

interface OpenFdaProduct {
  product_ndc?: string;
  generic_name?: string;
  brand_name?: string;
  active_ingredients?: OpenFdaActiveIngredient[];
  dosage_form?: string;
  packaging?: OpenFdaPackaging[];
}

interface OpenFdaFile {
  results: OpenFdaProduct[];
}

/** "50 mg/1" -> "50mg"; "1.25 mg/3mL" -> "1.25mg/3ml"; strips the "/1"
 * unit denominator (implicit "per one unit") to match the compact
 * "10mg" style strengths used elsewhere in this codebase. */
function normalizeStrength(raw: string): string {
  const cleaned = raw.replace(/\s+/g, '').toLowerCase();
  return cleaned.replace(/\/1$/, '');
}

function normalizeIngredientName(raw: string): string {
  return raw.trim().toLowerCase().replace(/\s+/g, ' ');
}

function buildConcept(product: OpenFdaProduct): LocalConcept | null {
  const ingredients = product.active_ingredients;
  if (!product.product_ndc || !ingredients || ingredients.length === 0 || !product.dosage_form) {
    return null;
  }
  // Some records have an ingredient entry with a missing name or
  // strength — skip the whole product rather than guess (a partial
  // ingredient set would corrupt the equivalence key).
  if (ingredients.some((ai) => !ai.name || !ai.strength)) {
    return null;
  }

  // Sort ingredients by normalized name so combo products always
  // produce the same canonical key regardless of the source array's
  // ordering (openFDA order is not guaranteed stable across labelers).
  const sorted = [...ingredients]
    .map((ai) => ({ name: normalizeIngredientName(ai.name), strength: normalizeStrength(ai.strength) }))
    .sort((a, b) => a.name.localeCompare(b.name));

  const ingredientJoined = sorted.map((i) => i.name).join(';');
  const strengthJoined = sorted.map((i) => i.strength).join(';');
  const doseForm = product.dosage_form.trim().toLowerCase();
  const displayName = (product.brand_name || product.generic_name || ingredientJoined).trim();

  return { displayName, ingredient: ingredientJoined, strength: strengthJoined, doseForm };
}

function main(): void {
  console.log(`Downloading ${SOURCE_URL} ...`);
  const workDir = mkdtempSync(path.join(tmpdir(), 'rx-verify-ndc-'));
  const zipPath = path.join(workDir, 'ndc.zip');

  try {
    const res = execSyncDownload(SOURCE_URL);
    writeFileSync(zipPath, res);

    console.log('Extracting...');
    execFileSync('unzip', ['-o', 'ndc.zip'], { cwd: workDir, stdio: 'inherit' });

    const extracted = path.join(workDir, 'drug-ndc-0001-of-0001.json');
    console.log('Parsing...');
    const raw = readFileSync(extracted, 'utf8');
    const parsed = JSON.parse(raw) as OpenFdaFile;
    console.log(`${parsed.results.length} product records`);

    const concepts: LocalConcept[] = [];
    const ndcIndex: Record<string, number> = {};
    let skippedProducts = 0;
    let skippedPackages = 0;

    for (const product of parsed.results) {
      const concept = buildConcept(product);
      if (!concept) {
        skippedProducts++;
        continue;
      }
      const conceptIndex = concepts.length;
      concepts.push(concept);

      for (const pkg of product.packaging ?? []) {
        const parsedNdc = parseNdc(pkg.package_ndc);
        if (!parsedNdc) {
          skippedPackages++;
          continue;
        }
        // If two products somehow claim the same package NDC, keep the
        // first (openFDA is expected to be internally consistent here).
        if (!(parsedNdc.normalized11 in ndcIndex)) {
          ndcIndex[parsedNdc.normalized11] = conceptIndex;
        }
      }
    }

    const out: LocalDrugData = {
      generatedAt: new Date().toISOString(),
      source: SOURCE_URL,
      concepts,
      ndcIndex
    };

    const compressed = gzipSync(Buffer.from(JSON.stringify(out), 'utf8'), { level: 9 });
    writeFileSync(OUTPUT_PATH, compressed);
    console.log(`Wrote ${OUTPUT_PATH} (${(compressed.length / 1024 / 1024).toFixed(1)} MB gzipped)`);
    console.log(
      `${concepts.length} concepts, ${Object.keys(ndcIndex).length} NDCs indexed ` +
        `(skipped ${skippedProducts} products with missing fields, ${skippedPackages} unparseable package NDCs)`
    );
  } finally {
    rmSync(workDir, { recursive: true, force: true });
  }
}

function execSyncDownload(url: string): Buffer {
  // Shell out to curl rather than depend on a fetch/streaming library —
  // this script is build-time-only (never shipped, never run by an end
  // user), so a hard dependency on system curl is an acceptable
  // tradeoff against adding an npm package for one script.
  return execFileSync('curl', ['-sL', '--max-time', '120', url], { maxBuffer: 1024 * 1024 * 1024 });
}

main();
