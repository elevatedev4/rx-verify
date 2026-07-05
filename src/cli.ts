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
 *
 * Output (stdout): a single JSON object, VerifyResult —
 *   { "verdicts": FieldVerdict[], "summary": VerifySummary }
 * on success, or on failure:
 *   { "error": string }  (also printed to stderr) with exit code 1.
 *
 * NOTE ON DRUG DATA: this CLI currently wires in FixtureProvider (see
 * src/drug/index.ts) — ~20 synthetic brand/generic concepts. Swapping in
 * real RxNorm data is an owner task (free NLM UTS account) documented in
 * the main README; once a real RxNormProvider exists, swap it in below —
 * nothing else in this file needs to change.
 *
 * This file intentionally does the minimum possible: read all of stdin,
 * JSON.parse, call verify(), JSON.stringify the result to stdout. No
 * logging of PHI-shaped content beyond echoing back exactly what the
 * caller sent (which is the caller's own local data — nothing is
 * transmitted anywhere by this process).
 */

import { verify } from './engine/index.js';
import { FixtureProvider } from './drug/index.js';
import type { ScriptData, EnteredData } from './types.js';

interface CliInput {
  source: ScriptData;
  entered: EnteredData;
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
    !('source' in parsed) ||
    !('entered' in parsed)
  ) {
    throw new Error('Input JSON must be an object with "source" and "entered" keys.');
  }

  const { source, entered } = parsed as CliInput;
  const provider = new FixtureProvider();
  const result = verify(source, entered, provider);

  process.stdout.write(JSON.stringify(result));
}

main().catch((err: Error) => {
  const message = err?.message ?? String(err);
  process.stderr.write(`verify-cli error: ${message}\n`);
  process.stdout.write(JSON.stringify({ error: message }));
  process.exitCode = 1;
});
