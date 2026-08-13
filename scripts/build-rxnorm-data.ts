/**
 * BUILD-TIME ONLY script. Run manually by a maintainer to refresh the
 * local RxNorm dataset — never invoked by the app at runtime (see
 * src/drug/rxnorm.ts RxNormDataProvider, which only ever reads the
 * committed output file, data/rxnorm-data.json.gz, with zero network
 * access).
 *
 * Downloads NLM's "RxNorm Current Prescribable Content" monthly release
 * — a public-domain subset of RxNorm requiring NO UMLS/UTS account or
 * license key (unlike the full RxNorm release) — and transforms it into
 * a compact local dataset keyed by normalized 11-digit NDC. See
 * src/drug/rxnorm-data-format.ts for the exact output shape and the
 * documented v1 scope limits (SCD/SBD only, no GPCK/BPCK).
 *
 * Usage:
 *   npx tsx scripts/build-rxnorm-data.ts
 *
 * Requires `curl` and `unzip` on PATH (present by default on
 * macOS/Linux) and network access; nothing else in this repo does at
 * runtime (see src/drug/index.ts's file header for the same rule
 * applied to scripts/build-drug-data.ts).
 *
 * WHAT THE PRESCRIBABLE SUBSET ACTUALLY CONTAINS (confirmed against a
 * real download, 2026-08): exactly three RRF files — RXNCONSO.RRF,
 * RXNSAT.RRF, RXNREL.RRF — a real subset of the full monthly release,
 * flagged CVF=4096. RXNREL IS present (this script's has_tradename
 * SBD->SCD derivation depends on it being there; an earlier NLM FAQ
 * revision reportedly described the subset as CONSO+SAT only, but the
 * live page as of this build documents CONSO+SAT+REL and REL was in
 * fact present in the downloaded zip).
 *
 * Source: https://www.nlm.nih.gov/research/umls/rxnorm/docs/prescribe.html
 * (RxNorm, public domain — NLM requests the release date be disclosed
 * since content changes monthly; see README.md's RxNorm section and
 * DEFAULT_ATTRIBUTION below).
 */

import { execFileSync } from 'node:child_process';
import { createReadStream, mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { gzipSync } from 'node:zlib';
import { parseNdc } from '../src/drug/index.js';
import { normalizeStrength, normalizeIngredientName } from './build-drug-data.js';
import type { RxNormConcept, RxNormData } from '../src/drug/rxnorm-data-format.js';

const RXNORM_FILES_PAGE = 'https://www.nlm.nih.gov/research/umls/rxnorm/docs/rxnormfiles.html';
const OUTPUT_PATH = path.join(import.meta.dirname, '..', 'data', 'rxnorm-data.json.gz');

export const DEFAULT_ATTRIBUTION =
  'Derived from the National Library of Medicine (NLM) RxNorm "Current Prescribable Content" ' +
  'monthly release. RxNorm is produced by NLM and is in the public domain; NLM requests that ' +
  'derivative works disclose the release this data was built from (see `source`/`generatedAt` ' +
  'on this object) since RxNorm content changes monthly.';

/**
 * Find the most recent RxNorm_full_prescribe_MMDDYYYY.zip download link
 * on NLM's "RxNorm Files" page — this IS the Current Prescribable
 * Content subset (confirmed: https://www.nlm.nih.gov/research/umls/
 * rxnorm/docs/prescribe.html links here for "download the subset").
 * Resolved at SCRIPT RUN TIME rather than hardcoded, since NLM publishes
 * a new one on the first Monday of every month and this script is meant
 * to be re-run periodically by a maintainer.
 */
export function findLatestPrescribeZipUrl(html: string): { url: string; releaseId: string } {
  const re = /https:\/\/download\.nlm\.nih\.gov\/rxnorm\/(RxNorm_full_prescribe_(\d{2})(\d{2})(\d{4})\.zip)/g;
  let best: { url: string; releaseId: string; sortKey: number } | null = null;
  let m: RegExpExecArray | null;
  while ((m = re.exec(html))) {
    const filename = m[1] as string;
    const mm = m[2] as string;
    const dd = m[3] as string;
    const yyyy = m[4] as string;
    const sortKey = Number(`${yyyy}${mm}${dd}`);
    if (!best || sortKey > best.sortKey) {
      best = {
        url: `https://download.nlm.nih.gov/rxnorm/${filename}`,
        releaseId: filename.replace(/\.zip$/, ''),
        sortKey
      };
    }
  }
  if (!best) {
    throw new Error(`Could not find any RxNorm_full_prescribe_*.zip link on ${RXNORM_FILES_PAGE}`);
  }
  return { url: best.url, releaseId: best.releaseId };
}

export interface ConsoRow {
  rxcui: string;
  sab: string;
  tty: string;
  str: string;
}

/** Parses one RXNCONSO.RRF line (pipe-delimited, trailing pipe). Column
 * positions per NLM's RxNorm technical documentation, section 12.4. */
export function parseConsoLine(line: string): ConsoRow | null {
  if (!line) return null;
  const f = line.split('|');
  const rxcui = f[0];
  const sab = f[11];
  const tty = f[12];
  const str = f[14];
  if (!rxcui || !sab || !tty || str === undefined || str === '') return null;
  return { rxcui, sab, tty, str };
}

export interface RelRow {
  rxcui1: string;
  rela: string;
  rxcui2: string;
}

/** Parses one RXNREL.RRF line. Column positions per section 12.7. */
export function parseRelLine(line: string): RelRow | null {
  if (!line) return null;
  const f = line.split('|');
  const rxcui1 = f[0];
  const rxcui2 = f[4];
  const rela = f[7];
  if (!rxcui1 || !rxcui2 || !rela) return null;
  return { rxcui1, rela, rxcui2 };
}

export interface SatNdcRow {
  rxcui: string;
  ndcRaw: string;
}

/** Parses one RXNSAT.RRF line, returning non-null ONLY for an ATN=NDC
 * attribute row. Column positions per section 12.5. */
export function parseSatNdcLine(line: string): SatNdcRow | null {
  if (!line) return null;
  const f = line.split('|');
  if (f[8] !== 'NDC') return null;
  const rxcui = f[0];
  const ndcRaw = f[10];
  if (!rxcui || !ndcRaw) return null;
  return { rxcui, ndcRaw };
}

/**
 * The full RxNorm Dose Form (TTY=DF) vocabulary present in THIS release
 * — a small (~120), closed, NLM-controlled set (e.g. "Oral Tablet",
 * "Extended Release Oral Tablet", "Injectable Solution"). Derived from
 * the actual downloaded data rather than hardcoded, so a future release
 * adding a new dose form is picked up automatically. Sorted by
 * DESCENDING length so buildRxNormConcept's suffix search tries the most
 * specific match first (e.g. "Extended Release Oral Tablet" before the
 * "Oral Tablet" it also ends with).
 */
export function extractDoseFormVocabulary(rows: ConsoRow[]): string[] {
  const set = new Set<string>();
  for (const r of rows) {
    if (r.sab === 'RXNORM' && r.tty === 'DF') {
      const str = r.str.trim();
      if (str) set.add(str);
    }
  }
  return [...set].sort((a, b) => b.length - a.length);
}

const BRACKET_SUFFIX_RE = /\s*\[[^\]]*\]\s*$/;

/**
 * Split one "/"-delimited ingredient segment of an SCD/SBD name (after
 * the dose form suffix has already been removed) into its ingredient
 * name and strength, e.g. "hydrochlorothiazide 50 MG" ->
 * { ingredient: "hydrochlorothiazide", strength: "50 MG" }.
 *
 * RxNorm's SCD/SBD naming convention always states the strength as a
 * trailing run of tokens starting with a digit (RXNCONSO section 12.4
 * examples: "lisinopril 10 MG Oral Tablet", "hydrochlorothiazide 50 MG /
 * triamterene 75 MG Oral Tablet") — so the first whitespace-delimited
 * token that starts with a digit marks the ingredient/strength boundary.
 * Returns null (never a guess) when no such token exists, or when it's
 * the very first token (no ingredient name at all) — both would indicate
 * a name shape this parser doesn't recognize, which must fall through to
 * "concept skipped", never a wrong split (this dataset's `strength`
 * field feeds a triple-equality comparison in src/drug/rxnorm.ts, so a
 * wrong split risks either a false GREEN or, at minimum, a corrupted
 * dataset entry).
 *
 * A real, sizeable category (~5000 rows in a 2026-08 release, mostly
 * injectables/patches) prepends a leading VOLUME or DURATION descriptor
 * before the ingredient name — a "{number} {unit word}" pair, e.g. "2 ML
 * digoxin 0.25 MG/ML" (a 2 mL vial) or "24 HR nifedipine 30 MG" (a
 * 24-hour extended-release tablet). That leading pair is NOT the
 * strength (the strength is always the segment's TRAILING digit run) —
 * so this strips at most one such leading pair (recognized as: first
 * token starts with a digit, AND a second token exists and is purely
 * alphabetic — never another number, which is what distinguishes it from
 * a digit-led ingredient name butting directly against its own strength)
 * before locating the trailing strength run.
 *
 * KNOWN LIMITATION (documented, not fixed): a small number of real
 * ingredient names themselves start with a digit (e.g. "17
 * alpha-hydroxyprogesterone caproate", "6-aminocaproic acid"). Those can
 * still be mistaken for the leading-descriptor shape above (a digit
 * token followed by an alphabetic token) — but the result is always
 * either a clean skip (this function or its caller returns null; see the
 * strengthStart<=0 guard below, which catches exactly this case since
 * stripping a REAL ingredient's leading tokens leaves nothing to find a
 * later strength boundary against) or a skip further up the pipeline,
 * never a wrong ingredient/strength value — consistent with this
 * feature's "skip rather than guess" rule everywhere else (e.g.
 * buildConcept in scripts/build-drug-data.ts skips a product with
 * missing fields rather than fabricate one).
 */
export function splitIngredientStrength(segment: string): { ingredient: string; strength: string } | null {
  let tokens = segment.trim().split(/\s+/).filter(Boolean);
  if (tokens.length < 2) return null;

  const first = tokens[0] as string;
  const second = tokens[1] as string | undefined;
  if (/^\d/.test(first) && second !== undefined && /^[a-zA-Z%]+$/.test(second) && tokens.length > 2) {
    tokens = tokens.slice(2);
  }
  if (tokens.length < 2) return null;

  // The strength is the trailing maximal run of digit-leading tokens.
  let strengthStart = -1;
  for (let i = tokens.length - 1; i >= 0; i--) {
    if (/^\d/.test(tokens[i] as string)) {
      strengthStart = i;
    } else if (strengthStart !== -1) {
      break;
    }
  }
  if (strengthStart <= 0) return null;

  return {
    ingredient: tokens.slice(0, strengthStart).join(' '),
    strength: tokens.slice(strengthStart).join(' ')
  };
}

/**
 * Build one RxNormConcept draft (everything except `scdRxcui`, which
 * requires a second pass over RXNREL — see deriveScdRxcuiMap) from a
 * single RXNCONSO row, or null when the row isn't a TTY=SCD/SBD RXNORM
 * row, or its STR doesn't parse cleanly against the known dose-form
 * vocabulary and the ingredient/strength grammar above. Never guesses:
 * any doubt skips the row entirely (it simply won't appear in the
 * dataset — the engine treats an unresolvable drug as unknown_drug
 * yellow either way, never a fabricated match).
 */
export function buildRxNormConcept(
  row: ConsoRow,
  doseFormVocabDesc: string[]
): Omit<RxNormConcept, 'scdRxcui'> | null {
  if (row.sab !== 'RXNORM') return null;
  if (row.tty !== 'SCD' && row.tty !== 'SBD') return null;

  const stripped = row.str.replace(BRACKET_SUFFIX_RE, '').trim();
  if (!stripped) return null;

  const strippedLower = stripped.toLowerCase();
  const doseForm = doseFormVocabDesc.find((df) => {
    const suffix = ` ${df}`.toLowerCase();
    return strippedLower.length > suffix.length && strippedLower.endsWith(suffix);
  });
  if (!doseForm) return null;

  const beforeForm = stripped.slice(0, stripped.length - doseForm.length).trim();
  if (!beforeForm) return null;

  const segments = beforeForm
    .split(' / ')
    .map((s) => s.trim())
    .filter(Boolean);
  if (segments.length === 0) return null;

  const parsedSegments = segments.map(splitIngredientStrength);
  if (parsedSegments.some((p) => p === null)) return null;

  const pairs = (parsedSegments as Array<{ ingredient: string; strength: string }>).map((p) => ({
    ingredient: normalizeIngredientName(p.ingredient),
    strength: normalizeStrength(p.strength)
  }));
  pairs.sort((a, b) => a.ingredient.localeCompare(b.ingredient));

  return {
    rxcui: row.rxcui,
    tty: row.tty as 'SCD' | 'SBD',
    displayName: row.str,
    ingredient: pairs.map((p) => p.ingredient).join(';'),
    strength: pairs.map((p) => p.strength).join(';'),
    doseForm: doseForm.toLowerCase()
  };
}

/**
 * SBD (branded) rxcui -> the RXCUI of the underlying SCD (generic
 * clinical drug) it's a tradename of, confirmed via RXNREL. Verified
 * empirically against a real 2026-08 release (e.g. SBD 92758
 * "griseofulvin 165 MG Oral Tablet [Fulvicin P/G]" carries a row
 * `92758|...|RB|245248|...|has_tradename|...`, and 245248 is indeed the
 * TTY=SCD "griseofulvin 165 MG Oral Tablet"): the has_tradename RELA,
 * read RXCUI1->RXCUI2, points FROM the SBD TO its SCD. Only trusted when
 * the target RXCUI is independently confirmed to be a TTY=SCD in this
 * same release (`scdRxcuis`) — an SBD can also relate to a coarser
 * SCDF/SBDF (dose-form-only, no strength) via other RELA values, which
 * this deliberately does NOT accept as a substitute for an exact-strength
 * SCD match. First relationship found per SBD wins; absent when none
 * confirms (left undefined on the concept, never guessed).
 */
export function deriveScdRxcuiMap(
  relRows: RelRow[],
  sbdRxcuis: ReadonlySet<string>,
  scdRxcuis: ReadonlySet<string>
): Map<string, string> {
  const map = new Map<string, string>();
  for (const r of relRows) {
    if (r.rela !== 'has_tradename') continue;
    if (map.has(r.rxcui1)) continue;
    if (!sbdRxcuis.has(r.rxcui1)) continue;
    if (!scdRxcuis.has(r.rxcui2)) continue;
    map.set(r.rxcui1, r.rxcui2);
  }
  return map;
}

export interface BuildRxNormStats {
  doseFormVocabCount: number;
  scdCount: number;
  sbdCount: number;
  skippedConceptRows: number;
  ndcCount: number;
  sbdWithScdLink: number;
  scdDisplayNameCount: number;
}

export interface BuildRxNormResult {
  data: RxNormData;
  stats: BuildRxNormStats;
}

/**
 * Pure in-memory transform: pre-parsed RXNCONSO/RXNREL/RXNSAT rows -> the
 * full RxNormData shape. No file/network access, so this is what tests
 * call directly against tiny synthetic fixture arrays — the
 * download/unzip/streaming machinery in main() below is
 * untestable-by-design (network + multi-hundred-MB files) and kept to
 * the thinnest possible wrapper around this function.
 *
 * `consoRows` may be the FULL RXNCONSO row set or (as main() does, for
 * memory) pre-filtered to TTY in {DF, SCD, SBD} — this function is
 * robust to either since it filters again internally.
 */
export function buildRxNormDataset(input: {
  consoRows: ConsoRow[];
  ndcRows: SatNdcRow[];
  relRows: RelRow[];
  generatedAt?: string;
  source?: string;
  attribution?: string;
}): BuildRxNormResult {
  const doseFormVocab = extractDoseFormVocabulary(input.consoRows);

  const conceptDrafts = new Map<string, Omit<RxNormConcept, 'scdRxcui'>>();
  const scdRxcuiSet = new Set<string>();
  const sbdRxcuiSet = new Set<string>();
  let skippedConceptRows = 0;

  for (const row of input.consoRows) {
    if (row.sab !== 'RXNORM') continue;
    if (row.tty === 'SCD') scdRxcuiSet.add(row.rxcui);
    if (row.tty === 'SBD') sbdRxcuiSet.add(row.rxcui);
    if (row.tty !== 'SCD' && row.tty !== 'SBD') continue;
    if (conceptDrafts.has(row.rxcui)) continue; // first RXNCONSO row per RXCUI wins
    const draft = buildRxNormConcept(row, doseFormVocab);
    if (!draft) {
      skippedConceptRows++;
      continue;
    }
    conceptDrafts.set(row.rxcui, draft);
  }

  const scdDisplayNames: Record<string, string> = {};
  for (const [rxcui, draft] of conceptDrafts) {
    if (draft.tty === 'SCD') scdDisplayNames[rxcui] = draft.displayName;
  }

  const scdRxcuiMap = deriveScdRxcuiMap(input.relRows, sbdRxcuiSet, scdRxcuiSet);

  const ndcToRxcui = new Map<string, string>();
  for (const row of input.ndcRows) {
    if (!conceptDrafts.has(row.rxcui)) continue; // only NDCs for concepts we actually built
    const parsed = parseNdc(row.ndcRaw);
    if (!parsed) continue;
    if (!ndcToRxcui.has(parsed.normalized11)) ndcToRxcui.set(parsed.normalized11, row.rxcui);
  }

  const rxcuisWithNdc = new Set(ndcToRxcui.values());
  const concepts: RxNormConcept[] = [];
  const rxcuiToIndex = new Map<string, number>();
  for (const rxcui of rxcuisWithNdc) {
    const draft = conceptDrafts.get(rxcui);
    if (!draft) continue;
    const scdRxcui = draft.tty === 'SBD' ? scdRxcuiMap.get(rxcui) : undefined;
    const idx = concepts.length;
    concepts.push(scdRxcui ? { ...draft, scdRxcui } : { ...draft });
    rxcuiToIndex.set(rxcui, idx);
  }

  const ndcIndex: Record<string, number> = {};
  for (const [ndc11, rxcui] of ndcToRxcui) {
    const idx = rxcuiToIndex.get(rxcui);
    if (idx !== undefined) ndcIndex[ndc11] = idx;
  }

  const data: RxNormData = {
    generatedAt: input.generatedAt ?? new Date().toISOString(),
    source: input.source ?? 'RxNorm Current Prescribable Content',
    attribution: input.attribution ?? DEFAULT_ATTRIBUTION,
    concepts,
    ndcIndex,
    scdDisplayNames
  };

  return {
    data,
    stats: {
      doseFormVocabCount: doseFormVocab.length,
      scdCount: concepts.filter((c) => c.tty === 'SCD').length,
      sbdCount: concepts.filter((c) => c.tty === 'SBD').length,
      skippedConceptRows,
      ndcCount: Object.keys(ndcIndex).length,
      sbdWithScdLink: concepts.filter((c) => c.tty === 'SBD' && c.scdRxcui).length,
      scdDisplayNameCount: Object.keys(scdDisplayNames).length
    }
  };
}

function execSyncDownload(url: string): Buffer {
  // Shell out to curl, same build-time-only tradeoff as
  // scripts/build-drug-data.ts's execSyncDownload.
  return execFileSync('curl', ['-sL', '--max-time', '300', url], { maxBuffer: 1024 * 1024 * 1024 });
}

async function readRelevantLines<T>(filePath: string, parse: (line: string) => T | null): Promise<T[]> {
  const out: T[] = [];
  const rl = createInterface({ input: createReadStream(filePath, { encoding: 'utf8' }), crlfDelay: Infinity });
  for await (const line of rl) {
    const parsed = parse(line);
    if (parsed !== null) out.push(parsed);
  }
  return out;
}

async function main(): Promise<void> {
  console.log(`Resolving latest RxNorm Current Prescribable Content release from ${RXNORM_FILES_PAGE} ...`);
  const html = execSyncDownload(RXNORM_FILES_PAGE).toString('utf8');
  const { url, releaseId } = findLatestPrescribeZipUrl(html);
  console.log(`Latest release: ${releaseId} (${url})`);

  const workDir = mkdtempSync(path.join(tmpdir(), 'rx-verify-rxnorm-'));
  try {
    console.log('Downloading (this is ~75MB, may take a minute)...');
    const zipBuf = execSyncDownload(url);
    const zipPath = path.join(workDir, 'rxnorm.zip');
    writeFileSync(zipPath, zipBuf);

    console.log('Extracting RXNCONSO.RRF, RXNSAT.RRF, RXNREL.RRF ...');
    execFileSync('unzip', ['-o', 'rxnorm.zip', 'rrf/RXNCONSO.RRF', 'rrf/RXNSAT.RRF', 'rrf/RXNREL.RRF'], {
      cwd: workDir,
      stdio: 'inherit'
    });

    const consoPath = path.join(workDir, 'rrf', 'RXNCONSO.RRF');
    const satPath = path.join(workDir, 'rrf', 'RXNSAT.RRF');
    const relPath = path.join(workDir, 'rrf', 'RXNREL.RRF');

    console.log('Parsing RXNCONSO.RRF (streamed, keeping only DF/SCD/SBD rows)...');
    const consoRows = await readRelevantLines(consoPath, (line) => {
      const row = parseConsoLine(line);
      if (!row) return null;
      if (row.sab !== 'RXNORM') return null;
      if (row.tty !== 'DF' && row.tty !== 'SCD' && row.tty !== 'SBD') return null;
      return row;
    });
    console.log(`  kept ${consoRows.length} DF/SCD/SBD rows`);

    console.log('Parsing RXNSAT.RRF (streamed, keeping only ATN=NDC rows) — this is the largest file, ~280MB...');
    const ndcRows = await readRelevantLines(satPath, parseSatNdcLine);
    console.log(`  kept ${ndcRows.length} NDC attribute rows`);

    console.log('Parsing RXNREL.RRF (streamed, keeping only RELA=has_tradename rows)...');
    const relRows = await readRelevantLines(relPath, (line) => {
      const row = parseRelLine(line);
      return row && row.rela === 'has_tradename' ? row : null;
    });
    console.log(`  kept ${relRows.length} has_tradename rows`);

    const { data, stats } = buildRxNormDataset({
      consoRows,
      ndcRows,
      relRows,
      generatedAt: new Date().toISOString(),
      source: releaseId
    });

    const compressed = gzipSync(Buffer.from(JSON.stringify(data), 'utf8'), { level: 9 });
    writeFileSync(OUTPUT_PATH, compressed);
    console.log(`Wrote ${OUTPUT_PATH} (${(compressed.length / 1024 / 1024).toFixed(2)} MB gzipped)`);
    console.log(
      `Dose form vocabulary: ${stats.doseFormVocabCount} known forms. ` +
        `${stats.scdCount} SCD + ${stats.sbdCount} SBD concepts built (skipped ${stats.skippedConceptRows} ` +
        `unparseable rows), ${stats.ndcCount} NDCs indexed, ${stats.sbdWithScdLink}/${stats.sbdCount} SBDs ` +
        `linked to a confirmed SCD via RXNREL has_tradename, ${stats.scdDisplayNameCount} SCD display names.`
    );
  } finally {
    rmSync(workDir, { recursive: true, force: true });
  }
}

// Only run when executed directly, never when imported for its pure
// functions (see build-drug-data.ts's identical guard).
const isMainModule = import.meta.url === pathToFileURL(process.argv[1] ?? '').href;
if (isMainModule) {
  main().catch((err) => {
    console.error(err);
    process.exitCode = 1;
  });
}
