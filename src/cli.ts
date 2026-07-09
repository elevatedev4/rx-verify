/**
 * verify-cli — a thin stdin/stdout JSON wrapper around the engine, so a
 * non-Node host (the Windows overlay, P0b) can call the tested engine as
 * a subprocess without any Node code of its own.
 *
 * Usage:
 *   node dist/cli.js < input.json > output.json
 *   echo '{"source": {...}, "entered": {...}}' | node dist/cli.js
 *
 * Input (stdin): a single JSON object
 *   { "source": ScriptData, "entered": EnteredData }
 * or (VerifyOCR v1 — see src/ocr/parseEscriptOcr.ts):
 *   { "ocr": OcrWord[], "entered": EnteredData }
 * ("source" is ignored if "ocr" is also present.)
 *
 * Output (stdout): a single JSON object, VerifyResult —
 *   { "verdicts": FieldVerdict[], "summary": VerifySummary }
 * on success, or on failure:
 *   { "error": string }  (also printed to stderr) with exit code 1.
 *
 * NOTE ON DRUG DATA: this CLI wires in LocalNdcProvider (see
 * src/drug/index.ts) — a real, local, offline dataset derived from the
 * public openFDA NDC directory (data/ndc-data.json.gz). It makes zero
 * network calls at lookup time. Precise RxNorm-rxcui equivalence (vs.
 * LocalNdcProvider's ingredient+strength+form approximation) is a
 * documented follow-on owner task (free NLM UTS account) — see the
 * header comment in src/drug/index.ts; once a real RxNormProvider
 * exists, swap it in below — nothing else in this file needs to change.
 *
 * This file intentionally does the minimum possible: read all of stdin,
 * JSON.parse, call verify(), JSON.stringify the result to stdout. No
 * logging of PHI-shaped content beyond echoing back exactly what the
 * caller sent (which is the caller's own local data — nothing is
 * transmitted anywhere by this process).
 */

import { verify } from './engine/index.js';
import { LocalNdcProvider, type RxNormProvider } from './drug/index.js';
import type { ScriptData, EnteredData } from './types.js';
import { parseEscriptOcr, type OcrWord } from './ocr/parseEscriptOcr.js';

/** Never consulted when skipDrugLookup is true (verify() skips compareDrugs entirely in that mode) — exists only so a provider value is always available to pass, without paying LocalNdcProvider's dataset-load cost. */
const NULL_PROVIDER: RxNormProvider = { getConcept: () => null };

interface CliInput {
  /**
   * Required unless `ocr` is present (see CliInput.ocr below and the
   * validation in main()) — kept for backward-compat with the original
   * UIA-tree-read version of the overlay, which always sends a fully
   * structured `source` itself.
   */
  source?: ScriptData;
  entered: EnteredData;
  /**
   * VerifyOCR v1: raw, position-aware OCR words (word + on-screen
   * bounding box) captured off the on-screen e-script pane — see
   * src/ocr/parseEscriptOcr.ts. When present, `source` is DERIVED from
   * this (via parseEscriptOcr) and any `source` value also sent is
   * ignored; when absent, the existing `source`-based path is used
   * unchanged (see overlay/RxVerifyOverlay/Uia/OcrFieldReader.cs, which
   * now sends `ocr` instead of a pre-parsed `source`).
   */
  ocr?: OcrWord[];
  /**
   * See VerifyOptions.skipDrugLookup (src/engine/index.ts). When true,
   * this process never constructs LocalNdcProvider at all — that's the
   * expensive part (loads + gunzips the ~130k-concept openFDA dataset),
   * not just the compareDrugs call — so a fast/non-drug refresh pays
   * none of that cost. The overlay sets this on its first, immediate
   * call per refresh; see overlay/RxVerifyOverlay/Engine/EngineClient.cs.
   */
  skipDrugLookup?: boolean;
}

function readStdin(): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    process.stdin.on('data', (c) => chunks.push(Buffer.from(c)));
    process.stdin.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    process.stdin.on('error', reject);
  });
}

async function main(): Promise<void> {
  const raw = await readStdin();

  if (!raw || !raw.trim()) {
    throw new Error('No input on stdin. Expected JSON: { "source": {...}, "entered": {...} }');
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (e) {
    throw new Error(`stdin was not valid JSON: ${(e as Error).message}`);
  }

  if (
    typeof parsed !== 'object' ||
    parsed === null ||
    !('entered' in parsed) ||
    !('source' in parsed || 'ocr' in parsed)
  ) {
    throw new Error('Input JSON must be an object with ("source" or "ocr") and "entered" keys.');
  }

  const { source, entered, ocr, skipDrugLookup } = parsed as CliInput;
  // VerifyOCR v1: when structured OCR words are provided, parse them
  // into the source record ourselves (see src/ocr/parseEscriptOcr.ts)
  // instead of trusting a pre-parsed `source` — the whole point of this
  // path is that OCR label/value association is safety-critical enough
  // to live here, tested, rather than in the untestable C# OCR string
  // parser it replaces. Any `source` also sent alongside `ocr` is
  // ignored.
  const resolvedSource = ocr ? parseEscriptOcr(ocr) : (source as ScriptData);

  // Only pay the LocalNdcProvider construction cost (dataset load +
  // gunzip) when a real drug lookup is actually going to happen — see
  // CliInput.skipDrugLookup doc above.
  const provider = skipDrugLookup ? NULL_PROVIDER : new LocalNdcProvider();
  const result = verify(resolvedSource, entered, provider, { skipDrugLookup });

  process.stdout.write(JSON.stringify(result));
}

main().catch((err: Error) => {
  const message = err?.message ?? String(err);
  process.stderr.write(`verify-cli error: ${message}\n`);
  process.stdout.write(JSON.stringify({ error: message }));
  process.exitCode = 1;
});
