/**
 * parseEscriptOcr — turns raw, position-aware OCR output of the on-screen
 * e-script pane into a PrescriptionRecord.
 *
 * SYNTHETIC DATA ONLY in this file and its tests.
 *
 * WHY THIS EXISTS (v1 of VerifyOCR): v0 (see overlay/RxVerifyOverlay/
 * Parsing/OcrEscriptParser.cs, now retired) parsed a flat OCR *string* in
 * C#, which cannot be unit-tested without Windows and had no way to use
 * word bounding boxes to disambiguate PioneerRx's actual on-screen layout
 * — a block of LABELS followed by a block of VALUES (see below) — from a
 * simple "Label: value" line. This module takes the OCR engine's raw
 * WORDS + bounding boxes instead of a flattened string, so it can:
 *   1. Reconstruct real lines/rows from geometry (words grouped by Y).
 *   2. Recognize field labels even when OCR mangles them (word-splitting,
 *      letter swaps) via a space-insensitive fuzzy match.
 *   3. Resolve label -> value by geometry, trying two known on-screen
 *      shapes and falling back between them.
 *   4. Use format PATTERNS (10-digit NPI, 11-digit NDC, dates, ZIP,
 *      phone) to catch/correct label<->value misassociation that pure
 *      position can get wrong — this is the safety-critical part, since
 *      a silently-swapped NPI/NDC is far worse than a blank field.
 *
 * NEVER THROWS: garbage/partial OCR input always returns a
 * PrescriptionRecord with nulls for whatever couldn't be found — see
 * parseEscriptOcr's top-level try/catch. A parsing bug must degrade to
 * "field not found" (yellow, "not provided", in the engine), never crash
 * the overlay or produce a confidently-wrong value.
 */

import type { Address, DrugDescriptor, Prescriber, PrescriptionRecord } from '../types.js';
import { parseDate } from '../normalize/date.js';

/** One OCR-recognized word plus its on-screen bounding box (screen pixels, origin top-left — matches Windows.Media.Ocr's OcrWord.BoundingRect). */
export interface OcrWord {
  text: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

// ---------------------------------------------------------------------
// Known field labels, in the fixed on-screen order the owner's dump
// showed them in (see branch brief "OBSERVED LAYOUT"). Order matters:
// in the labels-block-then-values-block layout, this is also the order
// values are matched positionally.
// ---------------------------------------------------------------------

type LabelKey =
  | 'patient'
  | 'address'
  | 'dob'
  | 'prescriber'
  | 'location'
  | 'phone'
  | 'written'
  | 'ndc'
  | 'medication'
  | 'quantity'
  | 'directions'
  | 'note'
  | 'substitutions'
  | 'refills'
  // Recognize-but-ignore labels (branch brief "RECOGNIZE-BUT-IGNORE"):
  // they don't map to any PrescriptionRecord field, but recognizing them
  // lets Pass A/B and trimValueNoise treat them as slot/value boundaries
  // instead of letting their text bleed into a neighboring real field.
  | 'gender'
  | 'agentName'
  | 'licenses'
  | 'supervisor'
  | 'spi'
  | 'ds2'
  | 'dxCodes'
  | 'order';

interface LabelDef {
  key: LabelKey;
  /** Canonical label text, already lowercase/space-free, used as the fuzzy-match target. */
  canonical: string;
  /** Max leading OCR words this label might be split across (OCR word-splitting noise, e.g. "Di recti ons" -> 3 words for "Directions"). */
  maxWords: number;
  /** Recognized as a label (bounds neighboring values / consumes a positional slot) but never assigned into the PrescriptionRecord. */
  ignore?: boolean;
}

// maxWords is deliberately 3 for every label (not just the longer ones):
// OCR word-splitting noise ("Pati ent:", "Di recti ons") can hit any
// label, not just long ones, and the fuzzy edit-distance check against
// each label's canonical form (see findLabelAtLineStart) already keeps
// this from over-matching into unrelated value text.
const LABELS: LabelDef[] = [
  { key: 'patient', canonical: 'patient', maxWords: 3 },
  { key: 'gender', canonical: 'gender', maxWords: 2, ignore: true },
  { key: 'address', canonical: 'address', maxWords: 3 },
  { key: 'dob', canonical: 'dob', maxWords: 3 },
  { key: 'prescriber', canonical: 'prescriber', maxWords: 3 },
  { key: 'agentName', canonical: 'agentname', maxWords: 2, ignore: true },
  { key: 'location', canonical: 'location', maxWords: 3 },
  { key: 'licenses', canonical: 'licenses', maxWords: 2, ignore: true },
  { key: 'phone', canonical: 'phone', maxWords: 3 },
  { key: 'supervisor', canonical: 'supervisor', maxWords: 2, ignore: true },
  { key: 'spi', canonical: 'spi', maxWords: 1, ignore: true },
  { key: 'written', canonical: 'written', maxWords: 3 },
  { key: 'ndc', canonical: 'ndc', maxWords: 3 },
  { key: 'medication', canonical: 'medication', maxWords: 3 },
  { key: 'quantity', canonical: 'quantity', maxWords: 3 },
  { key: 'directions', canonical: 'directions', maxWords: 3 },
  { key: 'refills', canonical: 'refills', maxWords: 2 },
  { key: 'ds2', canonical: 'ds', maxWords: 1, ignore: true },
  { key: 'note', canonical: 'note', maxWords: 3 },
  { key: 'substitutions', canonical: 'substitutions', maxWords: 3 },
  { key: 'dxCodes', canonical: 'dxcodes', maxWords: 3, ignore: true },
  { key: 'order', canonical: 'order', maxWords: 2, ignore: true }
];

/** Field keys whose inline/positional raw text is prone to absorbing a following recognized label or pharmacy-chrome token on the same OCR line (branch brief defect #3 — "Agent name"/"spr <SPI>"/"/ Mab" bleed). Trimmed via trimValueNoise before being stored. NOT applied to address/location — those get trailing bare-digit noise (a bled-in license number) stripped by parseAddressBlob's own targeted retry instead, since a generic trim there would also eat the address's own trailing ZIP digits. */
const NOISE_TRIM_KEYS = new Set<LabelKey>(['patient', 'prescriber', 'phone']);

/** Chrome/toolbar tokens actually observed (see branch brief) — used both as a defensive secondary filter (drop chrome LINES) and, via trimValueNoise, to truncate chrome words that bleed onto the END of a value already assigned to a NOISE_TRIM_KEYS field. Normalized (lowercase, alnum only). */
const CHROME_TOKENS = new Set([
  'dispense',
  'image',
  'new',
  'prescription',
  'ds',
  'escript',
  'dur',
  'more',
  'workflow',
  'claims',
  'rxedits',
  'edits',
  'fill',
  'audit',
  'original',
  'refilled',
  'time',
  'zoom',
  'select',
  'mab',
  'sn',
  'spi',
  'supervisor',
  'agent',
  'order',
  'refill',
  'replace'
]);

// ---------------------------------------------------------------------
// Small string utilities
// ---------------------------------------------------------------------

/** Lowercase, strip everything but a-z0-9 — makes label matching immune to OCR word-splitting/spacing and punctuation noise ("Escri pt" -> "escript", "CIaims" stays "ciaims" for edit-distance comparison). */
function normalize(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9]/g, '');
}

/** Standard Levenshtein edit distance, small-string DP (labels are short; fine to be simple/unoptimized). */
function levenshtein(a: string, b: string): number {
  const m = a.length;
  const n = b.length;
  if (m === 0) return n;
  if (n === 0) return m;
  const dp: number[] = new Array(n + 1);
  for (let j = 0; j <= n; j++) dp[j] = j;
  for (let i = 1; i <= m; i++) {
    let prev = dp[0] as number;
    dp[0] = i;
    for (let j = 1; j <= n; j++) {
      const temp = dp[j] as number;
      dp[j] = a[i - 1] === b[j - 1] ? prev : 1 + Math.min(prev, dp[j] as number, dp[j - 1] as number);
      prev = temp;
    }
  }
  return dp[n] as number;
}

/** Fuzzy-tolerance budget for a canonical label of the given normalized length — small labels get 1 char of slop, longer ones a bit more, so OCR noise ("Writtem", "CIaims"-style swaps) matches but unrelated words don't. */
function fuzzyThreshold(len: number): number {
  if (len <= 5) return 1;
  if (len <= 9) return 2;
  return 3;
}

/**
 * Used for CHROME_TOKENS matching only (isChromeLine / trimValueNoise).
 * Very short chrome tokens ("DS", "SN") are only 1 edit away from common
 * real content (ZIP-adjacent state codes "KS"/"MO", street-type "ST") —
 * fuzzy-matching those with the normal 1-char budget produces false
 * positives that wrongly classify a legitimate value line/word as chrome
 * (observed while adding "SN"/"Mab"/etc. per branch brief: "123 SYNTH ST
 * ... KS ..." tripped 2 false hits — "ST"~"SN" and "KS"~"DS" — and got
 * dropped as chrome). Tokens of length <=2 require an EXACT match here;
 * longer tokens keep the normal fuzzy budget.
 */
function isFuzzyMatch(candidate: string, canonical: string): boolean {
  if (!candidate) return false;
  if (canonical.length <= 2) return candidate === canonical;
  const dist = levenshtein(candidate, canonical);
  return dist <= fuzzyThreshold(canonical.length);
}

// ---------------------------------------------------------------------
// Line reconstruction from word bounding boxes
// ---------------------------------------------------------------------

/** Groups words into left-to-right, top-to-bottom "lines" (rows) by Y proximity. Words on the same visual row often land at slightly different Y from OCR jitter, so the tolerance is relative to word height, not a fixed pixel count. */
function groupLines(words: OcrWord[]): OcrWord[][] {
  const sorted = [...words].sort((a, b) => a.y - b.y || a.x - b.x);
  const lines: OcrWord[][] = [];

  for (const w of sorted) {
    const last = lines[lines.length - 1];
    if (last) {
      const avgY = last.reduce((sum, word) => sum + word.y, 0) / last.length;
      const avgH = last.reduce((sum, word) => sum + word.h, 0) / last.length || w.h || 1;
      if (Math.abs(w.y - avgY) <= avgH * 0.6) {
        last.push(w);
        continue;
      }
    }
    lines.push([w]);
  }

  for (const line of lines) line.sort((a, b) => a.x - b.x);
  return lines;
}

function wordsToText(words: OcrWord[]): string {
  return words
    .map((w) => w.text)
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
}

/** True if a leading colon-only token sits right after the label — stripped so it never leaks into the value. */
function stripLeadingColon(words: OcrWord[]): OcrWord[] {
  if (words.length === 0) return words;
  const first = words[0] as OcrWord;
  if (first.text.trim() === ':') return words.slice(1);
  if (first.text.trim().startsWith(':')) {
    return [{ ...first, text: first.text.trim().slice(1) }, ...words.slice(1)];
  }
  return words;
}

interface LabelMatch {
  label: LabelDef;
  /** Number of leading words consumed by the label itself. */
  consumed: number;
}

/** Tries to match a known label at the START of a line, trying 1..label.maxWords leading words concatenated (space-insensitive) against every label's canonical form, picking the closest fuzzy match under threshold. Returns null if nothing in the line's first few words looks like any known label. */
function findLabelAtLineStart(line: OcrWord[]): LabelMatch | null {
  let best: { label: LabelDef; consumed: number; ratio: number } | null = null;
  const maxConsume = Math.min(3, line.length);

  for (let consumed = 1; consumed <= maxConsume; consumed++) {
    const candidate = normalize(wordsToText(line.slice(0, consumed)));
    if (!candidate) continue;

    for (const label of LABELS) {
      if (consumed > label.maxWords) continue;
      const dist = levenshtein(candidate, label.canonical);
      if (dist > fuzzyThreshold(label.canonical.length)) continue;
      const ratio = dist / label.canonical.length;
      if (!best || ratio < best.ratio) {
        best = { label, consumed, ratio };
      }
    }
  }

  return best ? { label: best.label, consumed: best.consumed } : null;
}

/** Like findLabelAtLineStart, but tries a 1-3 word window starting at an arbitrary index (not just the start of a line) — used to find where a NEXT recognized label bleeds into the tail of a value that's already been assigned to a different field. */
function findLabelAt(words: OcrWord[], start: number): LabelMatch | null {
  let best: { label: LabelDef; consumed: number; ratio: number } | null = null;
  const maxConsume = Math.min(3, words.length - start);

  for (let consumed = 1; consumed <= maxConsume; consumed++) {
    const candidate = normalize(wordsToText(words.slice(start, start + consumed)));
    if (!candidate) continue;

    for (const label of LABELS) {
      if (consumed > label.maxWords) continue;
      const dist = levenshtein(candidate, label.canonical);
      if (dist > fuzzyThreshold(label.canonical.length)) continue;
      const ratio = dist / label.canonical.length;
      if (!best || ratio < best.ratio) {
        best = { label, consumed, ratio };
      }
    }
  }

  return best ? { label: best.label, consumed: best.consumed } : null;
}

/**
 * Truncates a value's word list at the first point (after its first word,
 * so a value is never trimmed to nothing) where either a known chrome
 * token or another recognized field label starts — the fix for branch
 * brief defect #3 ("noise bleeds into text values"): e.g. prescriber name
 * "Demo, Dana Agent name" -> "Demo, Dana", or prescriber phone
 * "(555) 555-0199 spr 1526938475001" -> "(555) 555-0199" (the "spr" OCR
 * mangle fuzzy-matches the "SPI" ignore-label). Only applied to
 * NOISE_TRIM_KEYS fields — see that const's doc for why address/location
 * are handled separately.
 */
function trimValueNoise(words: OcrWord[]): OcrWord[] {
  for (let i = 1; i < words.length; i++) {
    const w = words[i] as OcrWord;
    const norm = normalize(w.text);
    if (!norm) continue;

    let isChrome = false;
    for (const token of CHROME_TOKENS) {
      if (isFuzzyMatch(norm, token)) {
        isChrome = true;
        break;
      }
    }
    if (isChrome) return words.slice(0, i);

    if (findLabelAt(words, i)) return words.slice(0, i);
  }
  return words;
}

/** Defensive secondary chrome filter (see CHROME_TOKENS doc): a line is chrome if 2+ of its words fuzzily match known chrome tokens. */
function isChromeLine(line: OcrWord[]): boolean {
  let hits = 0;
  for (const w of line) {
    const norm = normalize(w.text);
    if (!norm) continue;
    for (const token of CHROME_TOKENS) {
      if (isFuzzyMatch(norm, token)) {
        hits++;
        break;
      }
    }
    if (hits >= 2) return true;
  }
  return false;
}

// ---------------------------------------------------------------------
// Value-string parsing helpers (address, quantity, substitutions)
// ---------------------------------------------------------------------

/**
 * Parses a single freeform address blob into components. Handles both
 * observed shapes: "<street> <city>, <ST> <zip>" (comma before state)
 * and "<street> <city> <ST><zip>" (no separators at all, OCR having
 * dropped the comma/space — e.g. "...FAKETOWN KS660000000"). ZIP is
 * reduced to its first 5 digits (see class doc / branch brief). Falls
 * back to treating the whole string as street-only if no trailing
 * "<state><zip>" shape is recognized — never throws, never guesses city
 * beyond the one trailing token before the state.
 */
const ADDRESS_RE = /^(.*?)[,\s]+([A-Za-z]{2})\s*(\d{5})(\d{4})?\s*$/;

function parseAddressBlob(raw: string): Address {
  const trimmed = raw.trim();
  if (!trimmed) return {};

  let m = ADDRESS_RE.exec(trimmed);
  if (!m) {
    // A bled-in noise token (e.g. a license number appended after the
    // real ZIP — branch brief defect #3) breaks the end anchor above.
    // Best-effort: strip ONE trailing bare 6+ digit token and retry once.
    const stripped = trimmed.replace(/\s+\d{6,}\s*$/, '');
    if (stripped !== trimmed) {
      m = ADDRESS_RE.exec(stripped);
    }
  }
  if (!m) return { street: trimmed };

  const before = (m[1] ?? '').trim();
  const state = (m[2] ?? '').toUpperCase();
  const zip = (m[3] ?? '').trim();

  const tokens = before.split(/\s+/).filter(Boolean);
  if (tokens.length < 2) {
    return { street: before || undefined, state: state || undefined, zip: zip || undefined };
  }

  const city = tokens[tokens.length - 1];
  const street = tokens.slice(0, -1).join(' ');
  return {
    street: street || undefined,
    city: city || undefined,
    state: state || undefined,
    zip: zip || undefined
  };
}

/** Unit tokens that mean "no real unit" and should fold to undefined rather than being stored (branch brief: "quantityUnit fold Unspecified/Unit/EA/each -> undefined"). Normalized (lowercase, alnum only). */
const QUANTITY_UNIT_FOLD = new Set(['unspecified', 'unit', 'ea', 'each']);

/** "50.0000 Unspecified" -> {quantity: "50.0000"} (unit folded away); "20 EA" -> {quantity: "20"} (EA folds too); "60 TABLET" -> {quantity: "60", quantityUnit: "TABLET"}. Mirrors the C# OcrEscriptParser.ApplyQuantity that this module supersedes, extended per branch brief to fold PioneerRx's non-units. */
function parseQuantity(raw: string): { quantity?: string; quantityUnit?: string } {
  const trimmed = raw.trim();
  if (!trimmed) return {};
  const parts = trimmed.split(/\s+/);
  const quantity = parts[0];
  const unitRaw = parts.slice(1).join(' ').trim();
  const quantityUnit = unitRaw && !QUANTITY_UNIT_FOLD.has(normalize(unitRaw)) ? unitRaw : undefined;
  return { quantity, quantityUnit };
}

/** "1 (additional refills)" -> "1". Only the leading integer is trusted; any trailing descriptive text is dropped. Returns undefined if the value doesn't start with a number. */
function parseRefills(raw: string): string | undefined {
  const m = /^(\d+)/.exec(raw.trim());
  return m ? m[1] : undefined;
}

/** Mirrors Models/EngineModels.cs SubstitutionsNotAllowed semantics — "not allowed"/DAW indicator => true; "allowed" => false; blank/ambiguous => undefined (never guessed). Checks "not allowed" before the bare "allowed" substring match. */
function parseSubstitutionsNotAllowed(raw: string | undefined): boolean | undefined {
  if (!raw || !raw.trim()) return undefined;
  const normalized = raw.trim().toLowerCase();
  if (normalized.includes('not allowed') || normalized === '1' || normalized.includes('daw')) return true;
  if (normalized.includes('allowed') || normalized === '0') return false;
  return undefined;
}

// ---------------------------------------------------------------------
// Pattern anchors — used to find/validate fields independent of (or to
// correct) label-geometry association. See class doc point 4.
// ---------------------------------------------------------------------

/** A bare (no punctuation) all-digit token of the given exact length, e.g. NPI=10. Deliberately requires the WHOLE word to be digits — a formatted phone number ("(555) 555-0100") keeps its punctuation in OCR text and will not collide with this. */
function findDigitToken(words: OcrWord[], length: number, exclude: Set<string>): string | null {
  for (const w of words) {
    const t = w.text.trim();
    if (t.length === length && /^\d+$/.test(t) && !exclude.has(t)) return t;
  }
  return null;
}

/** Real NDCs on the fixed e-script layout render DASHED (5-4-2 or similar, e.g. "00168-0203-60", "82619-0105-01") — v1 only looked for 11 bare digits and missed every one (branch brief defect #1). Matches either shape; the matched token (dashes included) is kept as-is per branch brief ("fine to keep the value as the matched token"). */
const NDC_DASHED_RE = /^\d{4,5}-\d{3,4}-\d{1,2}$/;

function findNdcToken(words: OcrWord[], exclude: Set<string>): string | null {
  for (const w of words) {
    const t = w.text.trim();
    if (exclude.has(t)) continue;
    if ((t.length === 11 && /^\d+$/.test(t)) || NDC_DASHED_RE.test(t)) return t;
  }
  return null;
}

/**
 * Best-effort repair for a written/DOB date whose "/" between day and
 * year got OCR-dropped, merging them into one run (branch brief defect
 * #2, e.g. "07/022026" should be "07/02/2026"): re-splits a trailing
 * 6-digit run into a 2-digit day + 4-digit year and re-inserts the "/".
 * Returns null (no repair attempted) for anything that doesn't match
 * this specific shape — never guesses beyond it.
 */
function repairMangledDate(raw: string): string | null {
  const m = /^(\d{1,2})\/(\d{2})(\d{4})$/.exec(raw.trim());
  if (!m) return null;
  return `${m[1]}/${m[2]}/${m[3]}`;
}

// ---------------------------------------------------------------------
// Main entry point
// ---------------------------------------------------------------------

/**
 * Parses structured OCR word output (word + bounding box) from the
 * captured e-script pane into a PrescriptionRecord. Never throws:
 * unrecognizable/partial/empty input returns a record with whatever WAS
 * found and undefined/null for the rest.
 */
export function parseEscriptOcr(ocr: OcrWord[] | null | undefined): PrescriptionRecord {
  const record: PrescriptionRecord = {};

  try {
    if (!ocr || ocr.length === 0) return record;
    const words = ocr.filter((w) => w && typeof w.text === 'string' && w.text.trim().length > 0);
    if (words.length === 0) return record;

    let lines = groupLines(words);

    // Drop toolbar/chrome: everything above the first recognized field
    // label (chrome always renders above the data pane — see branch
    // brief "OBSERVED LAYOUT").
    const firstLabelIdx = lines.findIndex((line) => findLabelAtLineStart(line) !== null);
    if (firstLabelIdx === -1) return record; // nothing recognizable at all
    lines = lines.slice(firstLabelIdx);

    // Defensive secondary chrome filter, in case a chrome-ish line
    // somehow sits below the first label line.
    lines = lines.filter((line) => !isChromeLine(line));

    // ---- Pass A: inline "Label: value" / "Label value" on the same row ----
    const raw: Partial<Record<LabelKey, string>> = {};
    const labelOrder: LabelKey[] = [];
    const leftoverLines: OcrWord[][] = [];

    for (const line of lines) {
      const match = findLabelAtLineStart(line);
      if (!match) {
        leftoverLines.push(line);
        continue;
      }
      labelOrder.push(match.label.key);
      const remainderWordsRaw = stripLeadingColon(line.slice(match.consumed));
      const remainderWords = NOISE_TRIM_KEYS.has(match.label.key)
        ? trimValueNoise(remainderWordsRaw)
        : remainderWordsRaw;
      const remainder = wordsToText(remainderWords);
      if (remainder) raw[match.label.key] = remainder;
    }

    // ---- Pass B: labels-block-then-values-block fallback ----
    // Any label that had no inline value (a label-only line) is matched
    // positionally, in encounter order, against the leftover (non-label)
    // lines — the shape produced when OCR flattens the pane into a block
    // of labels followed by a block of values.
    const missingLabels = labelOrder.filter((key) => raw[key] === undefined);
    missingLabels.forEach((key, i) => {
      const line = leftoverLines[i];
      if (line) {
        const words = NOISE_TRIM_KEYS.has(key) ? trimValueNoise(line) : line;
        const text = wordsToText(words);
        if (text) raw[key] = text;
      }
    });

    // ---- Pattern anchors: NPI / NDC, independent of any label ----
    // Per branch brief: NPI/NDC have no reliable label at all (or their
    // labeled slot can land on the wrong value after a block-layout
    // shift) — anchor them purely by exact digit-count/format, searching
    // every word in the recognized region. NDC accepts either 11 bare
    // digits or the dashed forms actually observed on the live layout
    // (branch brief defect #1) — see findNdcToken.
    const usedDigitTokens = new Set<string>();
    const npi = findDigitToken(lines.flat(), 10, usedDigitTokens);
    if (npi) usedDigitTokens.add(npi);
    const ndc = findNdcToken(lines.flat(), usedDigitTokens);
    if (ndc) usedDigitTokens.add(ndc);

    // ---- Date validation/correction for dob & written ----
    // If the label-associated value for either date field doesn't
    // actually parse as a date, search the pool of leftover value lines
    // (not already used for something else) for one that does — corrects
    // the block-layout-shift case where a value landed one slot off.
    const candidatePool = leftoverLines.map((l) => wordsToText(l)).filter(Boolean);
    const claimedFromPool = new Set<string>();
    // Anything already assigned via Pass A/B that came from the pool is
    // implicitly "claimed" so date-fallback search won't re-borrow it.
    for (const key of Object.keys(raw) as LabelKey[]) {
      const v = raw[key];
      if (v && candidatePool.includes(v)) claimedFromPool.add(v);
    }

    function resolveDateField(current: string | undefined): string | undefined {
      if (current) {
        if (parseDate(current)) return current;
        const repaired = repairMangledDate(current);
        if (repaired && parseDate(repaired)) return repaired;
      }
      for (const candidate of candidatePool) {
        if (claimedFromPool.has(candidate)) continue;
        if (parseDate(candidate)) {
          claimedFromPool.add(candidate);
          return candidate;
        }
        const repaired = repairMangledDate(candidate);
        if (repaired && parseDate(repaired)) {
          claimedFromPool.add(candidate);
          return repaired;
        }
      }
      // No label-associated value parsed (or repaired) as a date, and no
      // unclaimed pool candidate does either — better to leave the field
      // unset (surfaces as "not provided" in the engine) than to trust a
      // value that doesn't look like a date at all (see branch brief's
      // "never associate a misassigned value" pattern-validation goal).
      return undefined;
    }

    const dob = resolveDateField(raw.dob);
    const written = resolveDateField(raw.written);

    // ---- Assemble the PrescriptionRecord ----
    if (raw.patient) record.patientName = raw.patient;
    if (dob) record.patientDOB = dob;
    if (raw.address) record.patientAddress = parseAddressBlob(raw.address);
    if (written) record.dateWritten = written;

    if (raw.directions) record.sig = raw.directions;
    if (raw.note) {
      // NOTE: no `notes` field exists on PrescriptionRecord (types.ts) —
      // mirrors the same, already-documented gap in the retired C#
      // OcrEscriptParser ("Free-text Notes are NOT extracted"). Parsed
      // but intentionally dropped rather than invented a new field; see
      // branch report.
    }
    if (raw.substitutions !== undefined) {
      record.substitutionsNotAllowed = parseSubstitutionsNotAllowed(raw.substitutions);
    }
    if (raw.quantity) {
      const q = parseQuantity(raw.quantity);
      if (q.quantity) record.quantity = q.quantity;
      if (q.quantityUnit) record.quantityUnit = q.quantityUnit;
    }
    if (raw.refills) {
      const refills = parseRefills(raw.refills);
      if (refills) record.refills = refills;
    }

    const prescriber: Prescriber = {};
    if (raw.prescriber) prescriber.name = raw.prescriber;
    if (raw.phone) prescriber.phone = raw.phone;
    if (raw.location) prescriber.address = parseAddressBlob(raw.location);
    if (npi) prescriber.npi = npi;
    const hasPrescriberData =
      prescriber.name !== undefined ||
      prescriber.phone !== undefined ||
      prescriber.address !== undefined ||
      prescriber.npi !== undefined;
    if (hasPrescriberData) record.prescriber = prescriber;

    const drug: DrugDescriptor = {};
    if (raw.medication) drug.name = raw.medication;
    if (ndc) drug.ndc = ndc;
    const hasDrugData = drug.name !== undefined || drug.ndc !== undefined;
    if (hasDrugData) record.drug = drug;
  } catch {
    // Never throw — a parsing bug degrades to "field not found", not a
    // crashed overlay refresh. See class doc.
  }

  return record;
}
