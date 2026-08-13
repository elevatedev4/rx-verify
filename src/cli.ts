/**
 * verify-cli — a thin stdin/stdout JSON wrapper around the engine, so a
 * non-Node host (the Windows overlay, P0b) can call the tested engine as
 * a subprocess without any Node code of its own.
 *
 * TWO MODES:
 *
 * 1. One-shot (default, unchanged since P0b): one request per process.
 *      node dist/cli.js < input.json > output.json
 *      echo '{"source": {...}, "entered": {...}}' | node dist/cli.js
 *    Input (stdin): a single JSON object
 *      { "source": ScriptData, "entered": EnteredData }
 *    or (VerifyOCR v1 — see src/ocr/parseEscriptOcr.ts):
 *      { "ocr": OcrWord[], "entered": EnteredData }
 *    ("source" is ignored if "ocr" is also present.)
 *    Output (stdout): a single JSON object, VerifyResult —
 *      { "verdicts": FieldVerdict[], "summary": VerifySummary }
 *    on success, or on failure:
 *      { "error": string }  (also printed to stderr) with exit code 1.
 *
 * 2. Serve (`--serve`, latency fix — see overlay/RxVerifyOverlay/Engine/
 *    EngineClient.cs): one long-lived process handling many requests,
 *    line-delimited JSON over stdin/stdout — avoids paying Node's
 *    process-start cost (a few hundred ms) on every single verify call.
 *      node dist/cli.js --serve
 *    The FIRST stdout line, written before anything else and before any
 *    request is read, is a one-time ready/handshake line (RXVERIFY-
 *    TROUBLESHOOT 2026-08-13 — see readBuildInfo's doc for why):
 *      { "ready": true, "engineBuild": { "sha": string, "builtAt": string } }
 *    (never has an "id" key, unlike every response line below, so a
 *    caller can never mistake it for one). EngineClient.cs reads and
 *    consumes exactly this one line right after starting the process,
 *    before sending its first request.
 *    Each stdin LINE after that is one JSON request, same shape as the
 *    one-shot body above PLUS a required "id" (any string, echoed back
 *    verbatim so the caller can correlate a response to its request —
 *    the two sides never need to serialize calls, though EngineClient.cs
 *    currently does anyway). Each stdout LINE is one JSON response:
 *      { "id": string, "verdicts": [...], "summary": {...} }
 *    or on a per-request failure (bad JSON, missing "id", missing
 *    required keys, or a runtime error from verify() itself):
 *      { "id": string | null, "error": string }
 *    A bad request NEVER crashes the process or drops earlier/later
 *    requests — every line is handled independently, so one malformed
 *    line just gets an error response line while the process keeps
 *    serving. The process exits (code 0) when stdin closes (the parent
 *    disconnected the pipe, i.e. is shutting the child down).
 *
 * NOTE ON DRUG DATA: this CLI wires in LocalNdcProvider (see
 * src/drug/index.ts) — a real, local, offline dataset derived from the
 * public openFDA NDC directory (data/ndc-data.json.gz). It makes zero
 * network calls at lookup time. Precise RxNorm-rxcui equivalence (vs.
 * LocalNdcProvider's ingredient+strength+form approximation) is a
 * documented follow-on owner task (free NLM UTS account) — see the
 * header comment in src/drug/index.ts; once a real RxNormProvider
 * exists, swap it in below — nothing else in this file needs to change.
 * LocalNdcProvider itself caches the parsed dataset at MODULE scope (see
 * its constructor doc), so constructing a fresh instance per request in
 * --serve mode still only pays the gunzip/parse cost once per process,
 * not once per request.
 *
 * This file intentionally does the minimum possible: read stdin,
 * JSON.parse, call verify(), JSON.stringify the result back out. No
 * logging of PHI-shaped content beyond echoing back exactly what the
 * caller sent (which is the caller's own local data — nothing is
 * transmitted anywhere by this process).
 */

import { createInterface } from 'node:readline';
import { readFileSync } from 'node:fs';
import { verify } from './engine/index.js';
import { LocalNdcProvider, type RxNormProvider } from './drug/index.js';
import type { ScriptData, EnteredData, VerifyResult } from './types.js';
import { parseEscriptOcr, type OcrWord } from './ocr/parseEscriptOcr.js';

/**
 * Engine build stamp (RXVERIFY-TROUBLESHOOT 2026-08-13): npm run build's
 * prebuild step (scripts/generate-build-info.ts) writes dist/build-info.json
 * next to dist/cli.js — { sha, builtAt } for the exact commit and build
 * time that produced the dist/ this process is running from. A live
 * troubleshoot report showed a bug reproducing against the app's own
 * embedded git commit sha even though a from-scratch repro against the
 * TypeScript source AT that exact commit already came back correct — the
 * commit sha alone can't distinguish "the fix landed but dist/ was never
 * rebuilt" from "this is a genuine new regression". Read once at startup
 * (never changes while this process is alive) and surfaced exactly once,
 * in --serve's ready handshake below, so EngineClient.cs (and from
 * there, RxLogFormatter's log header) can report it.
 *
 * Read relative to THIS FILE's own compiled location (import.meta.url),
 * never the process's cwd — EngineClient.cs can invoke
 * `node <path>\dist\cli.js` from any working directory. Missing/
 * unreadable (dev mode running src/cli.ts directly via tsx, where no
 * dist/ sibling exists at all; or an install predating this feature)
 * falls back to "unknown" rather than failing the process — this is a
 * diagnostic nicety, never a hard dependency of verification itself.
 */
interface EngineBuildInfo {
  sha: string;
  builtAt: string;
}

function readBuildInfo(): EngineBuildInfo {
  try {
    const url = new URL('./build-info.json', import.meta.url);
    const parsed = JSON.parse(readFileSync(url, 'utf8')) as Partial<EngineBuildInfo>;
    return {
      sha: typeof parsed.sha === 'string' && parsed.sha ? parsed.sha : 'unknown',
      builtAt: typeof parsed.builtAt === 'string' && parsed.builtAt ? parsed.builtAt : 'unknown'
    };
  } catch {
    return { sha: 'unknown', builtAt: 'unknown' };
  }
}

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

/**
 * Shared shape check for both modes — throws the same message the
 * one-shot CLI has always thrown on a missing/malformed body. Kept as a
 * standalone assertion function (rather than inlined) so --serve's
 * per-line handling and main()'s one-shot handling can never drift on
 * what counts as a valid request.
 */
function validateCliInput(parsed: unknown): asserts parsed is CliInput {
  if (
    typeof parsed !== 'object' ||
    parsed === null ||
    !('entered' in parsed) ||
    !('source' in parsed || 'ocr' in parsed)
  ) {
    throw new Error('Input JSON must be an object with ("source" or "ocr") and "entered" keys.');
  }
}

/**
 * The actual verify() call, shared by both modes — everything past
 * "I have a validated CliInput" is identical regardless of which mode
 * produced it.
 */
function runVerify(input: CliInput): VerifyResult {
  const { source, entered, ocr, skipDrugLookup } = input;
  // VerifyOCR v1: when structured OCR words are provided, parse them
  // into the source record ourselves (see src/ocr/parseEscriptOcr.ts)
  // instead of trusting a pre-parsed `source` — the whole point of this
  // path is that OCR label/value association is safety-critical enough
  // to live here, tested, rather than in the untestable C# OCR string
  // parser it replaces. Any `source` also sent alongside `ocr` is
  // ignored.
  const resolvedSource = ocr ? parseEscriptOcr(ocr) : (source as ScriptData);

  // Only pay the LocalNdcProvider construction cost (dataset load +
  // gunzip, cached at module scope after the first call — see this
  // file's header doc) when a real drug lookup is actually going to
  // happen — see CliInput.skipDrugLookup doc above.
  const provider = skipDrugLookup ? NULL_PROVIDER : new LocalNdcProvider();
  return verify(resolvedSource, entered, provider, { skipDrugLookup });
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

  validateCliInput(parsed);
  const result = runVerify(parsed);

  process.stdout.write(JSON.stringify(result));
}

/**
 * One request line in --serve mode: same body as CliInput plus a
 * required "id", echoed back verbatim in the response line so the
 * caller (EngineClient.cs) can correlate a response to its request. See
 * this file's header doc for the full --serve contract.
 */
interface ServeRequest extends CliInput {
  id: string;
}

/**
 * --serve mode: line-delimited JSON request/response loop over
 * stdin/stdout. Uses node:readline (not a manual buffer scan) so
 * partial reads/chunk boundaries are handled correctly regardless of
 * how the parent process writes to the pipe.
 *
 * Every line is handled independently and defensively: a bad line
 * (invalid JSON, missing "id", missing required keys, or an exception
 * thrown out of verify() itself) produces one error response line and
 * the loop continues — it must NEVER take the whole process down, since
 * EngineClient.cs keeps this process alive across many refreshes and a
 * crash would mean paying process-start cost again right when the
 * pharmacist needs a fast verify.
 */
async function serve(): Promise<void> {
  // One-time ready/handshake line (RXVERIFY-TROUBLESHOOT 2026-08-13 —
  // see readBuildInfo's doc). Written synchronously, before the readline
  // interface below starts consuming stdin and before any request can
  // possibly be processed, so this is always the very first line
  // EngineClient.cs reads after starting the process — never racing
  // against (or being mistaken for) a real response line, which always
  // carries an "id" key that this line deliberately omits.
  process.stdout.write(JSON.stringify({ ready: true, engineBuild: readBuildInfo() }) + '\n');

  const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

  rl.on('line', (line) => {
    const trimmed = line.trim();
    if (!trimmed) return; // blank lines (e.g. a stray newline) are not requests, not errors

    let parsed: unknown;
    try {
      parsed = JSON.parse(trimmed);
    } catch (e) {
      // Can't recover an id from unparseable JSON — respond with id:
      // null rather than dropping the line silently, so the caller at
      // least sees SOMETHING went wrong instead of a hang.
      process.stdout.write(JSON.stringify({ id: null, error: `Request line was not valid JSON: ${(e as Error).message}` }) + '\n');
      return;
    }

    const id =
      typeof parsed === 'object' && parsed !== null && 'id' in parsed && typeof (parsed as { id: unknown }).id === 'string'
        ? (parsed as { id: string }).id
        : null;

    if (id === null) {
      process.stdout.write(JSON.stringify({ id: null, error: 'Request must be a JSON object with a string "id" field.' }) + '\n');
      return;
    }

    try {
      validateCliInput(parsed);
      const result = runVerify(parsed as ServeRequest);
      process.stdout.write(JSON.stringify({ id, ...result }) + '\n');
    } catch (e) {
      process.stdout.write(JSON.stringify({ id, error: (e as Error).message }) + '\n');
    }
  });

  // stdin closing means the parent (EngineClient.cs) is shutting this
  // process down deliberately — exit cleanly rather than hanging with
  // nothing left to read.
  await new Promise<void>((resolve) => rl.on('close', resolve));
}

if (process.argv.includes('--serve')) {
  serve().catch((err: Error) => {
    process.stderr.write(`verify-cli --serve fatal error: ${err?.message ?? String(err)}\n`);
    process.exitCode = 1;
  });
} else {
  main().catch((err: Error) => {
    const message = err?.message ?? String(err);
    process.stderr.write(`verify-cli error: ${message}\n`);
    process.stdout.write(JSON.stringify({ error: message }));
    process.exitCode = 1;
  });
}
