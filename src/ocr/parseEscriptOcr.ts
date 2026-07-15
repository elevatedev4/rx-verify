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
 *
 * GEOMETRY REMAP (this branch — see branch brief "remap everything"):
 * earlier tuning rounds (2a9bf13, 831cefc, 8d2789e) each patched one
 * symptom of the same root problem — Pass B's labels-block/values-block
 * pairing matched the Nth still-missing label against the Nth leftover
 * value line by ORDINAL POSITION IN A SHARED LIST, with no check that the
 * two actually sit in the same on-screen column. That's fine for a single
 * column of labels stacked above a single column of values (the owner's
 * most common shape, and every pre-existing fixture below), but silently
 * mispairs whenever two label sub-blocks render side by side (a
 * two-column panel) or the value block's internal row order doesn't
 * mirror the label block's — a wrong PATIENT/PRESCRIBER swap is far worse
 * than either field going blank. Pass B below now:
 *   1. x-clusters ALL recognized label occurrences into columns.
 *   2. x-clusters the leftover (non-label) lines into columns.
 *   3. Maps label-column rank -> value-column rank (leftmost to
 *      leftmost, etc. — tolerates a constant column OFFSET between the
 *      label block and the value block, only their ORDER must agree).
 *   4. Pairs a missing label only against leftover lines in ITS mapped
 *      column, in row order, via a per-column cursor — never against the
 *      whole flat leftover-line list.
 * Same-row multi-label groups (labelRowIds, e.g. "Quantity   Refills"
 * sharing one physical label-only row) still split their single matched
 * value row by column (splitLineByColumns), unchanged.
 */

import { appendFileSync, mkdirSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
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
  // Screen label variants actually observed (branch brief): "Refills
  // Authorized" / "Refills Remaining" are 10+ edits from plain "refills"
  // under the fuzzy threshold, so they need their own canonical entries
  // to match at all — otherwise only the leading "Refills" word matches,
  // leaving "Authorized"/"Remaining" behind as bogus "value" text that
  // shadows the real value.
  { key: 'refills', canonical: 'refillsauthorized', maxWords: 2 },
  { key: 'refills', canonical: 'refillsremaining', maxWords: 2 },
  // Source refills labeled "Total fills: N" (seen on responded
  // refill-request e-scripts) count the INITIAL fill plus refills, so
  // the refill count to compare against entered is N-1, not N — see
  // refillsFromTotalFills on PrescriptionRecord (types.ts) and
  // compareRefills (quantity/index.ts) for where that -1 is applied.
  { key: 'refills', canonical: 'totalfills', maxWords: 2 },
  { key: 'ds2', canonical: 'ds', maxWords: 1, ignore: true },
  { key: 'note', canonical: 'note', maxWords: 3 },
  { key: 'substitutions', canonical: 'substitutions', maxWords: 3 },
  { key: 'dxCodes', canonical: 'dxcodes', maxWords: 3, ignore: true },
  { key: 'order', canonical: 'order', maxWords: 2, ignore: true }
];

/** Field keys whose inline/positional raw text is prone to absorbing a following recognized label or pharmacy-chrome token on the same OCR line (branch brief defect #3 — "Agent name"/"spr <SPI>"/"/ Mab" bleed). Trimmed via trimValueNoise before being stored. NOT applied to address/location — those get trailing bare-digit noise (a bled-in license number) stripped by parseAddressBlob's own targeted retry instead, since a generic trim there would also eat the address's own trailing ZIP digits. */
const NOISE_TRIM_KEYS = new Set<LabelKey>(['patient', 'prescriber', 'phone']);

/** Horizontal gap (px) beyond which two consecutive words on the same reconstructed line are treated as belonging to different on-screen columns, not the same value. Chosen well above normal within-value word spacing (the widest normal gap observed between real sig words on the live capture was ~105px) but well below the far-column jump actually observed (~309px). Reused (same threshold) as the column-CLUSTERING distance for Pass B — a column boundary and a "different column" word gap are the same underlying signal at two different granularities (word-level vs line-start-level). */
const MAX_VALUE_WORD_GAP_PX = 150;

/**
 * Truncates a value's (already x-sorted) word list at the first point
 * where the gap between consecutive words exceeds MAX_VALUE_WORD_GAP_PX.
 * Like trimValueNoise, never trims to nothing (starts scanning from the
 * 2nd word). Applied to EVERY field's resolved value (not just
 * directions/sig, as in earlier tuning rounds) — branch brief root-cause
 * #3: "row-grouping jitter merges far-right columns into values" is
 * general to any field whose row happens to pick up a stray token from an
 * unrelated far-right column, not just sig.
 */
function trimColumnGap(words: OcrWord[]): OcrWord[] {
  for (let i = 1; i < words.length; i++) {
    const prev = words[i - 1] as OcrWord;
    const cur = words[i] as OcrWord;
    if (cur.x - (prev.x + prev.w) > MAX_VALUE_WORD_GAP_PX) return words.slice(0, i);
  }
  return words;
}

/**
 * Tighter, sig-specific column-gap threshold (px), applied ONLY to the
 * directions/sig field on top of the generic trimColumnGap above. A real
 * sig phrase's own internal word-to-word gaps are single-digit pixels
 * (plain word spacing within one run of text — see the live-capture
 * geometry in the fixture this branch was tuned against: "...for flares"
 * has gaps of 2-4px throughout). A right-column bleed (e.g. a bare
 * days-supply number landing to the right of the sig text with no
 * preceding "DS" label token to bound it via the embedded-label split
 * above) can land far closer than the generic MAX_VALUE_WORD_GAP_PX
 * (150px) column threshold — the observed gap was ~90px — while still
 * being obviously wider than any normal intra-sig word gap. Sig alone
 * gets this tighter, dedicated boundary rather than lowering
 * MAX_VALUE_WORD_GAP_PX globally (which is tuned against every other
 * field's own column-jump geometry, e.g. the ~309px jump in a different
 * fixture).
 */
const SIG_COLUMN_GAP_PX = 50;

function trimSigColumnGap(words: OcrWord[]): OcrWord[] {
  for (let i = 1; i < words.length; i++) {
    const prev = words[i - 1] as OcrWord;
    const cur = words[i] as OcrWord;
    if (cur.x - (prev.x + prev.w) > SIG_COLUMN_GAP_PX) return words.slice(0, i);
  }
  return words;
}

/**
 * Splits an (already x-sorted) line into column-groups wherever the
 * horizontal gap between consecutive words exceeds MAX_VALUE_WORD_GAP_PX —
 * same threshold/reasoning as trimColumnGap, but returns ALL segments
 * (not just the first). Used by Pass B to pair a single physical VALUE row
 * against MULTIPLE labels that shared one physical LABEL-ONLY row (see the
 * labelRowIds grouping in the main parse loop) — the real PioneerRx block
 * layout collapses "Quantity" + "Refills" (or similar label pairs) onto
 * one row in BOTH the labels block and the values block, so their values
 * land in the same on-screen row too, separated only by column position.
 */
function splitLineByColumns(line: OcrWord[]): OcrWord[][] {
  const segments: OcrWord[][] = [];
  let start = 0;
  for (let i = 1; i < line.length; i++) {
    const prev = line[i - 1] as OcrWord;
    const cur = line[i] as OcrWord;
    if (cur.x - (prev.x + prev.w) > MAX_VALUE_WORD_GAP_PX) {
      segments.push(line.slice(start, i));
      start = i;
    }
  }
  segments.push(line.slice(start));
  return segments;
}

/**
 * Assigns a column-cluster id (0 = leftmost) to each entry of `xs`, by
 * sorting on x and starting a new cluster whenever the gap to the
 * previous (x-sorted) entry exceeds `threshold`. Used by Pass B to group
 * BOTH label occurrences and leftover value lines into on-screen columns
 * — see class doc "GEOMETRY REMAP". Cluster ids are comparable across two
 * separate calls (e.g. one for labels, one for values) in the sense that
 * "cluster 0" always means "the leftmost cluster of ITS OWN input set" —
 * mapping label-cluster rank to value-cluster rank (not raw id equality)
 * is what actually pairs a label column to its value column; see
 * mapLabelClusterToValueCluster below.
 */
function clusterByX(xs: number[], threshold: number): number[] {
  const n = xs.length;
  const clusterOf = new Array<number>(n);
  const sortedIdx = xs.map((_, i) => i).sort((a, b) => (xs[a] as number) - (xs[b] as number));
  let clusterId = -1;
  let lastX = Number.NEGATIVE_INFINITY;
  for (const i of sortedIdx) {
    const x = xs[i] as number;
    if (x - lastX > threshold) clusterId++;
    clusterOf[i] = clusterId;
    lastX = x;
  }
  return clusterOf;
}

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
      // On an equal-quality fuzzy match, prefer the match that consumes
      // MORE of the line — e.g. "Refills Authorized" is an exact (ratio
      // 0) match for both the 1-word "refills" canonical and the 2-word
      // "refillsauthorized" canonical; consuming both words is correct
      // (the whole thing is the label), not leaving "Authorized" behind
      // to be mistaken for the value.
      if (!best || ratio < best.ratio || (ratio === best.ratio && consumed > best.consumed)) {
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
 * beyond the one trailing token before the state. The trailing
 * "<state><zip>" shape doubles as this field's format anchor (branch
 * brief item 2, "ZIP/state for addresses") — an address blob that never
 * matches it yields a street-only value rather than a guessed
 * city/state/zip split.
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

/** OCR letter-for-digit lookalikes, keyed by the mangled character actually observed (branch brief: Quantity/Refills/Written-date extraction consistently fails because a digit gets OCR'd as its lookalike letter, e.g. "1" -> "l", "0" -> "O"). */
const DIGIT_REPAIR_MAP: Record<string, string> = { O: '0', o: '0', l: '1', I: '1', S: '5', B: '8' };

/**
 * Best-effort OCR letter->digit repair for a token expected to be
 * numeric/date-shaped. Deliberately conservative and meant to be called
 * ONLY from numeric/date value-parsing call sites (parseQuantity,
 * parseRefills, repairMangledDate) — never on patient/prescriber names
 * or addresses, where a bare "O" or "l" is very likely real text:
 *
 *  - Any letter in the token OUTSIDE the lookalike set above (e.g. the
 *    "T"/"N" in "LOTION") means it's real text, not a mangled number —
 *    the whole token is returned untouched.
 *  - Even within the lookalike set, repair only fires when the token is
 *    already majority-digit, OR is short enough (<=2 alnum chars) that a
 *    majority-digit test can't be meaningful on its own (e.g. a lone "l"
 *    OCR'd for "1", with nothing else in the token to compare against).
 */
export function repairDigits(raw: string): string {
  let digitCount = 0;
  let mappableCount = 0;
  let otherLetterCount = 0;
  for (const ch of raw) {
    if (/[0-9]/.test(ch)) digitCount++;
    else if (ch in DIGIT_REPAIR_MAP) mappableCount++;
    else if (/[a-zA-Z]/.test(ch)) otherLetterCount++;
  }
  if (otherLetterCount > 0 || mappableCount === 0) return raw;
  const total = digitCount + mappableCount;
  if (digitCount < mappableCount && total > 2) return raw;

  let out = '';
  for (const ch of raw) out += DIGIT_REPAIR_MAP[ch] ?? ch;
  return out;
}

/** Unit tokens that mean "no real unit" and should fold to undefined rather than being stored (branch brief: "quantityUnit fold Unspecified/Unit/EA/each -> undefined"). Normalized (lowercase, alnum only). */
const QUANTITY_UNIT_FOLD = new Set(['unspecified', 'unit', 'ea', 'each']);

/**
 * "50.0000 Unspecified" -> {quantity: "50.0000"} (unit folded away); "20
 * EA" -> {quantity: "20"} (EA folds too); "60 TABLET" -> {quantity: "60",
 * quantityUnit: "TABLET"}. Mirrors the C# OcrEscriptParser.ApplyQuantity
 * that this module supersedes, extended per branch brief to fold
 * PioneerRx's non-units.
 *
 * The leading token is repaired for OCR letter-digit mangling ("5O.0000"
 * -> "50.0000") before being validated as a number (int or decimal); if
 * it still isn't numeric after repair, the following tokens are scanned
 * in order for the first one that is, so a mangled/non-numeric first
 * token doesn't blank the whole field when a real quantity value is
 * right there on the same line.
 */
const QUANTITY_NUMBER_RE = /^\d+(\.\d+)?$/;

function parseQuantity(raw: string): { quantity?: string; quantityUnit?: string } {
  const trimmed = raw.trim();
  if (!trimmed) return {};
  const parts = trimmed.split(/\s+/);

  let quantity: string | undefined;
  let unitStart = 1;
  for (let i = 0; i < parts.length; i++) {
    const repaired = repairDigits(parts[i] as string);
    if (QUANTITY_NUMBER_RE.test(repaired)) {
      quantity = repaired;
      unitStart = i + 1;
      break;
    }
  }
  if (!quantity) return {};

  const unitRaw = parts.slice(unitStart).join(' ').trim();
  const quantityUnit = unitRaw && !QUANTITY_UNIT_FOLD.has(normalize(unitRaw)) ? unitRaw : undefined;
  return { quantity, quantityUnit };
}

/**
 * "1 (additional refills)" -> "1". Only the leading integer is trusted;
 * any trailing descriptive text is dropped. The leading token is
 * repaired for OCR letter-digit mangling first (e.g. "l" OCR'd for "1"),
 * then validated as digits. Returns undefined if the (repaired) value
 * doesn't start with a number.
 */
function parseRefills(raw: string): string | undefined {
  const trimmed = raw.trim();
  const firstToken = trimmed.split(/\s+/)[0] ?? '';
  const repaired = repairDigits(firstToken);
  const m = /^(\d+)/.exec(repaired);
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
function findDigitToken(words: OcrWord[], length: number, exclude: Set<string>): OcrWord | null {
  for (const w of words) {
    const t = w.text.trim();
    if (t.length === length && /^\d+$/.test(t) && !exclude.has(t)) return w;
  }
  return null;
}

/** Real NDCs on the fixed e-script layout render DASHED (5-4-2 or similar, e.g. "00168-0203-60", "82619-0105-01") — v1 only looked for 11 bare digits and missed every one (branch brief defect #1). Matches either shape; the matched token (dashes included) is kept as-is per branch brief ("fine to keep the value as the matched token"). */
const NDC_DASHED_RE = /^\d{4,5}-\d{3,4}-\d{1,2}$/;

function findNdcToken(words: OcrWord[], exclude: Set<string>): OcrWord | null {
  for (const w of words) {
    const t = w.text.trim();
    if (exclude.has(t)) continue;
    if ((t.length === 11 && /^\d+$/.test(t)) || NDC_DASHED_RE.test(t)) return w;
  }
  return null;
}

/**
 * Phone-number shape anchor (branch brief item 2). Deliberately lenient —
 * this validates "looks phone-shaped enough to trust", not "is a
 * perfectly OCR'd US phone number": a live capture can mangle the
 * leading "(" away entirely (e.g. "(555)" -> "085)"), and that's still a
 * real, usable phone value, just imperfectly read. What it rejects is a
 * value that isn't phone-shaped AT ALL (stray label/address text that
 * slipped past geometry pairing) — per the NO-GUESS policy (item 3),
 * that's worse to keep than to blank.
 */
const PHONE_RE = /^\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}$/;

function isPhoneShaped(raw: string): boolean {
  return PHONE_RE.test(raw.trim());
}

/**
 * Best-effort repair for a mangled written/DOB date. Handles, in order:
 *  1. OCR letter-for-digit mangling anywhere in the token ("O7/02/2026"
 *     -> "07/02/2026") via repairDigits.
 *  2. Dash/dot/space separators instead of "/" ("07-02-2026",
 *     "07.02.2026", "07 02 2026") — normalized to slashes, but only when
 *     the whole token is already digit-triple-shaped, so this never
 *     touches unrelated text that happens to contain a dash.
 *  3. "MM/DD/YYY Y" — a stray OCR space splitting the last year digit
 *     off — collapsed back together.
 *  4. "MM/DDYYYY" — the "/" between day and year got OCR-dropped
 *     (branch brief defect #2, e.g. "07/022026" -> "07/02/2026").
 *  5. "MMDD/YYYY" — the "/" between month and day got OCR-dropped.
 * Returns null (no repair attempted/needed) if the token doesn't match
 * any of the above and wasn't changed by digit/separator repair — never
 * guesses beyond these specific shapes.
 */
function repairMangledDate(raw: string): string | null {
  const trimmed = raw.trim();
  let candidate = repairDigits(trimmed);

  if (/^\d{1,4}[\s.-]\d{1,2}[\s.-]\d{2,4}$/.test(candidate)) {
    candidate = candidate.replace(/[\s.-]/g, '/');
  }

  let m = /^(\d{1,2})\/(\d{1,2})\/(\d{3})\s(\d)$/.exec(candidate);
  if (m) return `${m[1]}/${m[2]}/${m[3]}${m[4]}`;

  m = /^(\d{1,2})\/(\d{2})(\d{4})$/.exec(candidate);
  if (m) return `${m[1]}/${m[2]}/${m[3]}`;

  m = /^(\d{2})(\d{2})\/(\d{4})$/.exec(candidate);
  if (m) return `${m[1]}/${m[2]}/${m[3]}`;

  return candidate !== trimmed ? candidate : null;
}

// ---------------------------------------------------------------------
// Diagnostics (branch brief item 4) — a compact, machine-readable
// per-field summary of HOW each field resolved (or why it didn't),
// appended to the same daily log file mechanism the overlay's OcrLogger
// (overlay/RxVerifyOverlay/Ocr/OcrLogger.cs) already writes to
// (%TEMP%\VerifyOCR\ocr-<yyyyMMdd>.log on Windows / the OS temp dir's
// VerifyOCR subfolder elsewhere) — same directory/filename convention,
// so a read's raw-word dump (logged by the C# overlay before invoking
// this CLI) and this parse's field-resolution summary (logged here,
// right after) land in the same file for the next debugging pass. Never
// allowed to affect parsing or throw — see appendOcrDiagnosticsLog.
// ---------------------------------------------------------------------

export interface FieldDiagnostic {
  /** PrescriptionRecord-shaped field name, e.g. "patientName", "prescriber.npi". */
  field: string;
  status: 'resolved' | 'miss';
  /** How the value was found. Absent when status is 'miss'. */
  strategy?: 'inline-row' | 'block-column' | 'pattern-anchor' | 'pattern-anchor-fallback';
  label?: { text: string; x: number; y: number };
  value?: { text: string; x: number; y: number };
  /** Machine-readable miss reason. Absent when status is 'resolved'. */
  reason?: string;
}

/** Caps a value's logged text so a long sig/address doesn't blow up a "few lines per field" diagnostics block. */
function truncateForLog(text: string, max = 60): string {
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

function formatDiagnosticLine(d: FieldDiagnostic): string {
  if (d.status === 'miss') {
    return `  ${d.field}: MISS(${d.reason ?? 'unresolved'})`;
  }
  const labelPart = d.label
    ? `label "${truncateForLog(d.label.text, 24)}"@(${Math.round(d.label.x)},${Math.round(d.label.y)})`
    : 'label (none — pattern anchor)';
  const valuePart = d.value
    ? `value "${truncateForLog(d.value.text)}"@(${Math.round(d.value.x)},${Math.round(d.value.y)})`
    : 'value (unknown position)';
  return `  ${d.field}: ${labelPart} -> ${valuePart} [${d.strategy ?? 'unknown'}]`;
}

/** The field-resolution lines only (no timestamp header) — used both to render the block body and, unchanged run-to-run for an identical resolution outcome, as the DE-DUP comparison key below (a fresh ISO timestamp on every call would otherwise never compare equal). */
function diagnosticsContentKey(entries: FieldDiagnostic[]): string {
  return entries.map(formatDiagnosticLine).join('\n');
}

/** Builds the full per-parse diagnostics block text (pure function — no I/O — so it's independently unit-testable without touching the filesystem). */
export function buildDiagnosticsBlock(entries: FieldDiagnostic[]): string {
  const header = `[${new Date().toISOString()}] parseEscriptOcr field resolution (${entries.length} fields)`;
  return `${header}\n${diagnosticsContentKey(entries)}\n`;
}

const DIAGNOSTICS_LOG_DIR = path.join(tmpdir(), 'VerifyOCR');

/** Per-day log file size cap before rotation (truncation) kicks in — mirrors OcrLogger.cs's own MaxLogFileBytes (see its class doc "SIZE CAP"): a long shift with lots of distinct reads can't grow this diagnostics log without bound either. */
const DIAGNOSTICS_MAX_LOG_BYTES = 5 * 1024 * 1024; // ~5 MB

/**
 * Raw text of the last successfully LOGGED diagnostics block's CONTENT
 * (see diagnosticsContentKey — not the full block, so the timestamp
 * doesn't defeat the comparison), mirroring OcrLogger.cs's own
 * _lastLoggedRawText de-dup guard: the overlay re-reads the same
 * on-screen Rx roughly once a second, and an unchanged field-resolution
 * outcome isn't worth a new block. Module-scoped, so this only de-dupes
 * WITHIN one running process — see appendOcrDiagnosticsLog doc for the
 * one-parse-per-CLI-subprocess caveat.
 */
let lastLoggedDiagnosticsContent: string | undefined;

function diagnosticsLogFilePath(): string {
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return path.join(DIAGNOSTICS_LOG_DIR, `ocr-${y}${m}${d}.log`);
}

/**
 * Best-effort append of one parse's field-resolution diagnostics to the
 * shared daily log — see class doc above. NEVER throws (mirrors
 * OcrLogger.cs's own best-effort guarantee: a locked/unwritable log file
 * must never take down a live verification pass) and is skipped entirely
 * under the test runner (VITEST_WORKER_ID is set by vitest) so running
 * the suite doesn't scribble PHI-shaped synthetic fixtures onto the
 * developer's real OS temp dir on every run — buildDiagnosticsBlock
 * above is exported and unit-tested directly instead.
 *
 * Subject to the same two guards as OcrLogger.cs (overlay/RxVerifyOverlay/
 * Ocr/OcrLogger.cs, class doc "BOUNDED PHI LOG"):
 *  1. DE-DUP: a resolution outcome byte-identical to the last LOGGED one
 *     is skipped entirely — see lastLoggedDiagnosticsContent above. (Note:
 *     cli.ts is invoked as a fresh subprocess per call — see its header
 *     doc — so this module-level guard only de-dupes consecutive parses
 *     within one long-lived process, e.g. tests or a future in-process
 *     host; it's a no-op, not a regression, for the current one-shot-CLI
 *     usage, where the size cap below is what actually bounds growth.)
 *  2. SIZE CAP: before any append, if today's file has already grown
 *     past DIAGNOSTICS_MAX_LOG_BYTES, it's rotated (truncated to a short
 *     marker line) first — same truncate-in-place approach as
 *     OcrLogger.cs's RotateIfOversized, not a numbered rollover.
 */
function appendOcrDiagnosticsLog(entries: FieldDiagnostic[]): void {
  if (process.env.VITEST_WORKER_ID) return;
  try {
    const contentKey = diagnosticsContentKey(entries);
    if (contentKey === lastLoggedDiagnosticsContent) return;
    lastLoggedDiagnosticsContent = contentKey;

    mkdirSync(DIAGNOSTICS_LOG_DIR, { recursive: true });
    const filePath = diagnosticsLogFilePath();

    let existingSize = 0;
    try {
      existingSize = statSync(filePath).size;
    } catch {
      existingSize = 0; // file doesn't exist yet — nothing to rotate.
    }
    if (existingSize > DIAGNOSTICS_MAX_LOG_BYTES) {
      writeFileSync(
        filePath,
        `[${new Date().toISOString()}] --- log rotated: exceeded ${
          DIAGNOSTICS_MAX_LOG_BYTES / (1024 * 1024)
        } MB, earlier entries in this file truncated ---\n\n`
      );
    }

    appendFileSync(filePath, buildDiagnosticsBlock(entries));
  } catch {
    // Best-effort diagnostic logging only — see class doc.
  }
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
  const diagnostics: FieldDiagnostic[] = [];

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
    // somehow sits below the first label line. Never drop a line whose
    // START is a recognized, non-ignore field label, though — that means
    // it carries real field data (a live capture showed the Quantity
    // label+value and an inline Refills label+value sharing one merged
    // OCR row; "Refills"/"refills)" alone fuzzy-match 2 CHROME_TOKENS
    // entries meant for the "Refilled 1 time" tab-strip, tripping the
    // 2-hit rule below and silently dropping the Quantity value too).
    lines = lines.filter((line) => {
      const startMatch = findLabelAtLineStart(line);
      if (startMatch && !startMatch.label.ignore) return true;
      return !isChromeLine(line);
    });

    // ---- Pass A: inline "Label: value" / "Label value" on the same row ----
    const raw: Partial<Record<LabelKey, string>> = {};
    const labelOrder: LabelKey[] = [];
    // Parallel to labelOrder: the on-screen position of the label word(s)
    // themselves (first consumed word's x/y) plus the label text actually
    // matched — used only for diagnostics (item 4) and for x-clustering
    // labels into columns in Pass B (item 1) below.
    const labelPositions: { x: number; y: number; text: string }[] = [];
    // Parallel to labelOrder: the id of the ORIGINAL physical row each
    // pushed label came from (see rowIdCounter below). Two labels that
    // shared one physical row (label-only, no inline values between them —
    // e.g. "Quantity   Refills", or "NDC   Medication" / "Note
    // Substitutions" on the real PioneerRx block layout) get the SAME
    // rowId even though they're processed as separate loop iterations via
    // the pendingLines re-queue below. Pass B uses this to know when
    // several missing labels must be paired against ONE shared leftover
    // value row (split by column) instead of one leftover row each.
    const labelRowIds: number[] = [];
    // Parallel to labelOrder: the specific canonical LABEL VARIANT that
    // matched (e.g. 'refills' vs 'refillsauthorized' vs 'totalfills' —
    // several distinct canonicals share the same LabelKey). Only consumed
    // below to detect the 'totalfills' variant specifically (Change 3 —
    // "Total fills: N" means N-1 refills), but kept general in case a
    // future field needs the same per-variant distinction.
    const labelCanonicals: string[] = [];
    const leftoverLines: OcrWord[][] = [];
    // How each raw[key] was actually resolved — populated at every
    // assignment site (Pass A inline, Pass B block-column) and consumed
    // only by the diagnostics block at the end.
    const resolutionMeta: Partial<Record<LabelKey, { strategy: FieldDiagnostic['strategy']; words: OcrWord[] }>> =
      {};

    // Queue instead of a plain for-of: a single physical OCR row can carry
    // MORE THAN ONE label (observed on the live capture — Quantity's
    // label+value on the left, Refills' label+value further right, all
    // grouped into one row by Y proximity; or, on the pure labels-block
    // layout, two labels with NO value text between them at all). When a
    // second real label is found — either partway through the first
    // label's value, or immediately at the start of the remainder — the
    // tail is re-queued (carrying the SAME rowId) so the loop processes it
    // as an independent label on its next iteration.
    let rowIdCounter = 0;
    const pendingLines: { words: OcrWord[]; rowId: number }[] = lines.map((l) => ({
      words: l,
      rowId: rowIdCounter++
    }));
    while (pendingLines.length > 0) {
      const { words: line, rowId } = pendingLines.shift() as { words: OcrWord[]; rowId: number };
      const match = findLabelAtLineStart(line);
      if (!match) {
        leftoverLines.push(line);
        continue;
      }
      labelOrder.push(match.label.key);
      labelRowIds.push(rowId);
      labelCanonicals.push(match.label.canonical);
      const labelWords = line.slice(0, match.consumed);
      const firstLabelWord = labelWords[0] as OcrWord;
      labelPositions.push({ x: firstLabelWord.x, y: firstLabelWord.y, text: wordsToText(labelWords) });
      const remainderWordsRaw = stripLeadingColon(line.slice(match.consumed));

      let splitIdx = -1;
      const remainderStartsWithLabel = remainderWordsRaw.length > 0 ? findLabelAt(remainderWordsRaw, 0) : null;
      // Require an EXACT (not fuzzy) match against the canonical label text
      // here — this decides "is the remainder actually a value, or is it
      // really the START of the NEXT label", and the fuzzy tolerance used
      // everywhere else is too permissive for that call: e.g. real value
      // text "not allowed" fuzzy-matches the 'note' label ("not" is 1 edit
      // from "note", well under its threshold), which would wrongly treat
      // an ordinary Substitutions value line as a label-only row. A plain
      // label word (no inline value) reliably matches EXACTLY, since it's
      // literally the label's own on-screen text with no OCR mangling
      // simulated in these fixtures' geometry-only distinction; requiring
      // an exact match keeps this generalization from misfiring on value
      // text that merely resembles a label.
      const remainderStartsWithExactLabel =
        remainderStartsWithLabel &&
        !remainderStartsWithLabel.label.ignore &&
        normalize(wordsToText(remainderWordsRaw.slice(0, remainderStartsWithLabel.consumed))) ===
          remainderStartsWithLabel.label.canonical;
      if (remainderStartsWithExactLabel) {
        // General case (branch brief): a label-only physical row can carry
        // MORE THAN ONE recognized label with NO value text between them
        // at all (e.g. "Quantity   Refills", "NDC   Medication", "Note
        // Substitutions"). The remainder starts immediately with the NEXT
        // label, so none of it is this label's value — re-queue the whole
        // remainder (same rowId) so it's matched as its own label next,
        // leaving BOTH labels correctly "missing" for Pass B's positional
        // pairing instead of the first label swallowing the second
        // label's text as a bogus value.
        splitIdx = 0;
      } else {
        for (let i = 1; i < remainderWordsRaw.length; i++) {
          const embedded = findLabelAt(remainderWordsRaw, i);
          // Generalized (branch brief "OCR address extraction" — was
          // narrowly restricted to the 'refills' key only, kept from the
          // original 831cefc fix). A right-hand column's label always
          // bounds the current field's value: e.g. patientAddress's
          // "Address:" row also carries a separate "Phone" column further
          // right on the SAME physical OCR row when the on-screen gap
          // between them is under MAX_VALUE_WORD_GAP_PX (trimColumnGap
          // alone doesn't catch that case). Requiring an EXACT (not fuzzy)
          // canonical match here — same rigor as remainderStartsWithExactLabel
          // above — is what keeps this generalization from misfiring on
          // ordinary multi-word value text that merely resembles a label
          // (e.g. "not allowed" fuzzy-matching 'note'): a real label's own
          // on-screen text matches its canonical form exactly, with no OCR
          // mangling simulated in these fixtures' geometry-only
          // distinction.
          // Deliberately NOT excluding ignore-labels here (unlike
          // remainderStartsWithExactLabel above): the LABELS doc
          // (~lines 111-112) says ignore labels are meant to "bound
          // neighboring values", and a right-column ignore-label (e.g.
          // "Agent name" sitting right of a prescriber-name row, or a
          // bare "DS" right of a sig row) must still stop the current
          // field's value from bleeding into it, even though (per
          // `ignore`) it will never itself be emitted as a field — only
          // its POSITION is used as a boundary here.
          if (
            embedded &&
            embedded.label.key !== match.label.key &&
            normalize(wordsToText(remainderWordsRaw.slice(i, i + embedded.consumed))) === embedded.label.canonical
          ) {
            splitIdx = i;
            break;
          }
        }
      }

      let remainderWords = remainderWordsRaw;
      if (splitIdx !== -1) {
        remainderWords = remainderWordsRaw.slice(0, splitIdx);
        pendingLines.unshift({ words: remainderWordsRaw.slice(splitIdx), rowId });
      }

      if (NOISE_TRIM_KEYS.has(match.label.key)) remainderWords = trimValueNoise(remainderWords);
      // Bound EVERY field's inline value by the next on-screen column
      // (branch brief item 2 — "bound the token span by neighboring
      // labels' geometry so bleed can't append"), not just directions/sig.
      remainderWords = trimColumnGap(remainderWords);
      // Sig gets an ADDITIONAL, tighter column-stop on top of the generic
      // one above — see trimSigColumnGap doc.
      if (match.label.key === 'directions') remainderWords = trimSigColumnGap(remainderWords);

      const remainder = wordsToText(remainderWords);
      if (remainder) {
        raw[match.label.key] = remainder;
        resolutionMeta[match.label.key] = { strategy: 'inline-row', words: remainderWords };
      }
    }

    // Tracks every leftover-line index consumed by ANY of the mechanisms
    // below (sig continuation, Pass B geometry pairing, prescriber-address
    // continuation) — a single leftover row can only ever be consumed
    // once. Declared here (before Pass B) so the sig-continuation gather
    // immediately below can reserve its rows BEFORE Pass B's own
    // column-ranked pairing gets a chance to hand them to an unrelated
    // label that happens to have no inline value of its own (e.g. a bare
    // "Note" label-only row, which — like any other still-missing label —
    // Pass B would otherwise try to fill from the nearest unconsumed
    // leftover line, stealing a sig-wrap continuation row before this
    // block ever sees it).
    const consumedLeftover = new Set<number>();

    // ---- Sig wrapped continuation lines (branch brief "sig multi-line") ----
    // The sig/directions value can word-wrap onto additional physical OCR
    // rows below its own row (groupLines splits on Y gap, so a wrapped
    // continuation of the directions text lands as its own leftover line,
    // never consumed by Pass A). Bounded by (1) the directions label's
    // own row Y, (2) the Y of the NEXT recognized label row — whatever it
    // happens to be ("Note" on the live capture, but nothing here assumes
    // that literal label, so an unexpected field can't be swallowed), and
    // (3) the continuation line sitting in the same left-x column as the
    // directions value — a Note/Substitutions row (or anything else
    // further down) can never be mistaken for a sig continuation, both
    // because it falls outside the Y bound and because (being itself a
    // recognized label) it was never a leftover line to begin with. Run
    // BEFORE Pass B (see consumedLeftover doc above) so Pass B's
    // column-ranked pairing can't steal a continuation row out from under
    // a still-unresolved label like a bare "Note".
    if (raw.directions) {
      const directionsIdx = labelOrder.findIndex((k) => k === 'directions');
      const directionsMeta = resolutionMeta.directions;
      if (directionsIdx !== -1 && directionsMeta) {
        const directionsRowY = (labelPositions[directionsIdx] as { x: number; y: number; text: string }).y;
        const valueX = (directionsMeta.words[0] as OcrWord).x;
        let nextLabelY = Number.POSITIVE_INFINITY;
        for (const p of labelPositions) {
          if (p.y > directionsRowY + 1 && p.y < nextLabelY) nextLabelY = p.y;
        }
        const continuationTexts: string[] = [];
        const continuationWordsAll: OcrWord[] = [];
        for (let i = 0; i < leftoverLines.length; i++) {
          if (consumedLeftover.has(i)) continue;
          const line = leftoverLines[i] as OcrWord[];
          const first = line[0] as OcrWord;
          if (first.y <= directionsRowY + 1 || first.y >= nextLabelY) continue;
          if (Math.abs(first.x - valueX) > MAX_VALUE_WORD_GAP_PX / 2) continue;
          consumedLeftover.add(i);
          const trimmedLine = trimSigColumnGap(trimColumnGap(line));
          const text = wordsToText(trimmedLine);
          if (text) {
            continuationTexts.push(text);
            continuationWordsAll.push(...trimmedLine);
          }
        }
        if (continuationTexts.length > 0) {
          raw.directions = [raw.directions, ...continuationTexts].join(' ');
          resolutionMeta.directions = {
            strategy: directionsMeta.strategy,
            words: [...directionsMeta.words, ...continuationWordsAll]
          };
        }
      }
    }

    // ---- Pass B: labels-block-then-values-block fallback ----
    // Any label that had no inline value (a label-only line) is resolved
    // by GEOMETRY, not by ordinal position in a shared list — see class
    // doc "GEOMETRY REMAP". Missing labels and leftover (non-label) lines
    // are each x-clustered into on-screen columns; a label's column is
    // mapped (by rank, leftmost-to-leftmost) to its corresponding value
    // column, and within that pairing a label is matched to the value
    // line directly below it in row order via a per-column cursor.
    // Same-row multi-label groups (labelRowIds) still consume ONE shared
    // leftover row, split by column (splitLineByColumns) — unchanged.
    const missingIdx: number[] = [];
    for (let idx = 0; idx < labelOrder.length; idx++) {
      if (raw[labelOrder[idx] as LabelKey] === undefined) missingIdx.push(idx);
    }

    const labelClusterOf = clusterByX(
      labelPositions.map((p) => p.x),
      MAX_VALUE_WORD_GAP_PX
    );
    const leftoverXs = leftoverLines.map((l) => (l[0] as OcrWord).x);
    const valueClusterOf = clusterByX(leftoverXs, MAX_VALUE_WORD_GAP_PX);
    const valueClusterCount = leftoverLines.length > 0 ? Math.max(...valueClusterOf) + 1 : 0;

    // Leftover line indices grouped by value-column, each list kept in
    // original (top-to-bottom) row order.
    const leftoverIdxByCluster: number[][] = Array.from({ length: valueClusterCount }, () => []);
    leftoverLines.forEach((_, i) => {
      (leftoverIdxByCluster[valueClusterOf[i] as number] as number[]).push(i);
    });
    // Per-column cursor: the next not-yet-consumed leftover line in that
    // value column. Consuming per-column (instead of one global pointer
    // across the whole flat list) is what stops an unrelated column's
    // lines from being handed to a label that has nothing to do with
    // them — see the "extra far-right column" / "jumbled two-column"
    // fixtures in the test suite.
    const cursorByCluster: number[] = new Array(valueClusterCount).fill(0);

    function mappedValueCluster(labelCluster: number): number | null {
      if (valueClusterCount === 0) return null;
      // Rank-mapped, not equal-x-mapped: tolerates the value block being
      // a constant column OFFSET to the right/left of the label block
      // (branch brief fixture: "value column offset from label column")
      // — only the RELATIVE column order has to agree.
      return Math.min(labelCluster, valueClusterCount - 1);
    }

    // consumedLeftover (tracking every leftover-line index consumed by
    // ANY mechanism, including this pass's own column-mapped primary
    // attempt and fallback below) is declared earlier, before the sig
    // continuation gather — see its doc above.

    /** Primary attempt: next not-yet-consumed leftover line in THIS value column, in row order. */
    function pickFromCluster(valueCluster: number): number | null {
      const bucket = leftoverIdxByCluster[valueCluster] as number[];
      let cursor = cursorByCluster[valueCluster] as number;
      while (cursor < bucket.length) {
        const idx = bucket[cursor] as number;
        cursor++;
        if (!consumedLeftover.has(idx)) {
          cursorByCluster[valueCluster] = cursor;
          return idx;
        }
      }
      cursorByCluster[valueCluster] = cursor;
      return null;
    }

    /**
     * REVIEW FIX (blocking finding, round 1): the column-mapped primary
     * attempt above can starve a real single-column capture whose
     * leftover lines happen to x-scatter into >1 cluster from ordinary
     * bbox jitter, right-justified numerics, or inconsistent indentation
     * — e.g. one label column (labelCluster always 0) but a Quantity
     * value row that lands far enough right to form its own value-
     * cluster; every missing label maps to value-cluster 0 only, so that
     * off-cluster row is never assigned even though nothing else wants
     * it either. Geometry pairing is meant to be STRICTLY ADDITIVE over
     * plain ordinal (next-leftover-line-in-row-order) pairing — it may
     * resolve cases ordinal gets wrong (fixture (o)), but must never
     * resolve FEWER fields than ordinal would have. So: whenever the
     * column-mapped attempt finds nothing (its column has no more
     * candidates, or the label has no mapped column at all), fall back
     * to the next not-yet-consumed leftover line in plain top-to-bottom
     * row order across ALL columns — exactly what main's ordinal Pass B
     * always did. This only ever adds coverage: the column-mapped
     * result is used as-is whenever it succeeds.
     */
    function pickFallback(): number | null {
      for (let i = 0; i < leftoverLines.length; i++) {
        if (!consumedLeftover.has(i)) return i;
      }
      return null;
    }

    function assignGroup(groupKeys: LabelKey[], lineIdx: number): void {
      consumedLeftover.add(lineIdx);
      const line = leftoverLines[lineIdx] as OcrWord[];
      const segments = groupKeys.length > 1 ? splitLineByColumns(line) : [line];
      groupKeys.forEach((key, g) => {
        const seg = segments[g];
        if (!seg || seg.length === 0) return;
        let segWords = NOISE_TRIM_KEYS.has(key) ? trimValueNoise(seg) : seg;
        segWords = trimColumnGap(segWords);
        if (key === 'directions') segWords = trimSigColumnGap(segWords);
        const text = wordsToText(segWords);
        if (text) {
          raw[key] = text;
          resolutionMeta[key] = { strategy: 'block-column', words: segWords };
        }
      });
    }

    interface PendingGroup {
      groupKeys: LabelKey[];
      labelCluster: number;
    }
    const pendingGroups: PendingGroup[] = [];
    {
      let mi = 0;
      while (mi < missingIdx.length) {
        let mj = mi;
        while (
          mj + 1 < missingIdx.length &&
          labelRowIds[missingIdx[mj + 1] as number] === labelRowIds[missingIdx[mi] as number]
        ) {
          mj++;
        }
        const groupIdxs = missingIdx.slice(mi, mj + 1);
        const groupKeys = groupIdxs.map((idx) => labelOrder[idx] as LabelKey);
        const firstIdx = groupIdxs[0] as number;
        pendingGroups.push({ groupKeys, labelCluster: labelClusterOf[firstIdx] as number });
        mi = mj + 1;
      }
    }

    /**
     * REVIEW FIX (blocking finding, round 2): pickFallback used to fire
     * INLINE, per group, in label-encounter order — so a label that
     * happened to be encountered (and starved) BEFORE a later label's
     * value even entered the leftover pool's "current" cursor position
     * could steal that later label's still-untouched, correctly
     * column-matched row (reviewer repro: Patient -> Prescriber -> Phone
     * (absent) -> DOB, two-column layout; Phone's exhausted column
     * triggered fallback and stole DOB's real, later, column-0 value
     * before DOB ever got its own turn). Fixed by running this as TWO
     * STRICT PASSES over every group: first give EVERY group its
     * column-mapped primary attempt (so a label's own well-matched value
     * is never up for grabs by an earlier-encountered, differently-
     * columned label); only once every primary attempt has run does
     * ANY group fall back to "next remaining leftover line, any column".
     */
    const unresolvedGroups: PendingGroup[] = [];
    for (const group of pendingGroups) {
      const valueCluster = mappedValueCluster(group.labelCluster);
      const lineIdx = valueCluster !== null ? pickFromCluster(valueCluster) : null;
      if (lineIdx !== null) {
        assignGroup(group.groupKeys, lineIdx);
      } else {
        unresolvedGroups.push(group);
      }
    }
    for (const group of unresolvedGroups) {
      const lineIdx = pickFallback();
      if (lineIdx !== null) assignGroup(group.groupKeys, lineIdx);
    }

    // ---- Prescriber address wrapped 2nd line (branch brief "OCR address
    // extraction", bug 2) ----
    // groupLines splits rows on a Y gap beyond ~avgH*0.6, so a wrapped
    // "city, state zip" continuation line rendered a row or two below the
    // "Prescriber Location:" value (e.g. "4477 MOCKAVE PL" / "FAKEVILLE, KS
    // 990047213") lands as its own leftover line, never consumed by Pass
    // A/B. Deliberately SCOPED to the 'location' key only (not applied to
    // patientAddress, which the real capture never wraps) and GEOMETRIC/
    // PATTERN-based, not layout-hardcoded: bounded purely by (1) the
    // location label's own row Y, (2) the next recognized label's row Y
    // (whatever it happens to be — "Phone" here, but nothing about this
    // logic assumes that literal label), (3) the continuation line sitting
    // in the same left-x column as the location value, and (4) the
    // continuation line's text itself looking like an address tail (a
    // state abbreviation AND a 5- or 9-digit ZIP), not just any nearby
    // line — a Note/Substitutions row two lines down must never be
    // mistaken for an address continuation.
    const ADDRESS_CONTINUATION_RE = /\b[A-Z]{2}\b/;
    const ZIP_TAIL_RE = /\b\d{5}(\d{4})?\b/;
    if (raw.location) {
      const locationIdx = labelOrder.findIndex((k) => k === 'location');
      const locationMeta = resolutionMeta.location;
      if (locationIdx !== -1 && locationMeta) {
        const locationRowY = (labelPositions[locationIdx] as { x: number; y: number; text: string }).y;
        const valueX = (locationMeta.words[0] as OcrWord).x;
        let nextLabelY = Number.POSITIVE_INFINITY;
        for (const p of labelPositions) {
          if (p.y > locationRowY + 1 && p.y < nextLabelY) nextLabelY = p.y;
        }
        let bestIdx = -1;
        for (let i = 0; i < leftoverLines.length; i++) {
          if (consumedLeftover.has(i)) continue;
          const line = leftoverLines[i] as OcrWord[];
          const first = line[0] as OcrWord;
          if (first.y <= locationRowY + 1 || first.y >= nextLabelY) continue;
          if (Math.abs(first.x - valueX) > MAX_VALUE_WORD_GAP_PX / 2) continue;
          const text = wordsToText(line);
          if (!ADDRESS_CONTINUATION_RE.test(text) || !ZIP_TAIL_RE.test(text)) continue;
          bestIdx = i;
          break;
        }
        if (bestIdx !== -1) {
          consumedLeftover.add(bestIdx);
          const continuationWords = leftoverLines[bestIdx] as OcrWord[];
          raw.location = `${raw.location} ${wordsToText(continuationWords)}`;
          resolutionMeta.location = {
            strategy: locationMeta.strategy,
            words: [...locationMeta.words, ...continuationWords]
          };
        }
      }
    }

    // ---- Pattern anchors: NPI / NDC, independent of any label ----
    // Per branch brief: NPI/NDC have no reliable label at all (or their
    // labeled slot can land on the wrong value after a block-layout
    // shift) — anchor them purely by exact digit-count/format, searching
    // every word in the recognized region. NDC accepts either 11 bare
    // digits or the dashed forms actually observed on the live layout
    // (branch brief defect #1) — see findNdcToken.
    const usedDigitTokens = new Set<string>();
    const npiWord = findDigitToken(lines.flat(), 10, usedDigitTokens);
    if (npiWord) usedDigitTokens.add(npiWord.text.trim());
    const ndcWord = findNdcToken(lines.flat(), usedDigitTokens);
    if (ndcWord) usedDigitTokens.add(ndcWord.text.trim());
    const npi = npiWord?.text.trim();
    const ndc = ndcWord?.text.trim();

    // ---- Date validation/correction for dob & written ----
    // If the label-associated value for either date field doesn't
    // actually parse as a date, search the pool of leftover value lines
    // (not already used for something else) for one that does — corrects
    // the block-layout-shift case where a value landed one slot off.
    const candidatePool = leftoverLines.map((l) => ({ text: wordsToText(l), pos: l[0] as OcrWord })).filter(
      (c) => c.text
    );
    /**
     * REVIEW FIX (blocking finding, round 2, "contamination path"): a
     * field's raw text only actually consumed its pool row if that
     * field's OWN validator (where it has one) accepts it — a value that
     * gets rejected (e.g. failed isPhoneShaped) must fully release its
     * row, not silently block a DIFFERENT field's legitimate pool
     * fallback from ever claiming it. Reuses the exact same validators
     * the assembly section below applies, so "claims the pool row" and
     * "survives into the record" can never disagree. dob/written have no
     * entry in FIELD_POOL_VALIDATORS (their own validation is date-shape,
     * handled by resolveDateField itself below) so they pass through
     * this gate unconditionally — but they're deliberately NOT skipped
     * from the loop entirely: dob's own already-resolved raw text must
     * still self-claim its pool row, or written's fallback search below
     * would be free to re-borrow the exact same row dob just used (and
     * vice versa).
     */
    const FIELD_POOL_VALIDATORS: Partial<Record<LabelKey, (v: string) => boolean>> = {
      phone: isPhoneShaped,
      quantity: (v) => Boolean(parseQuantity(v).quantity),
      refills: (v) => Boolean(parseRefills(v)),
      substitutions: (v) => parseSubstitutionsNotAllowed(v) !== undefined
    };
    const claimedFromPool = new Set<string>();
    // Anything already assigned via Pass A/B that came from the pool AND
    // will actually survive its own field's validation is implicitly
    // "claimed" so date-fallback search won't re-borrow it.
    for (const key of Object.keys(raw) as LabelKey[]) {
      const v = raw[key];
      if (!v) continue;
      const validator = FIELD_POOL_VALIDATORS[key];
      if (validator && !validator(v)) continue; // rejected — fully release the row.
      if (candidatePool.some((c) => c.text === v)) claimedFromPool.add(v);
    }

    function resolveDateField(
      key: 'dob' | 'written'
    ): { value: string | undefined; strategy: FieldDiagnostic['strategy']; pos?: OcrWord } {
      const current = raw[key];
      if (current) {
        const meta = resolutionMeta[key];
        const pos = meta?.words[0];
        if (parseDate(current)) return { value: current, strategy: meta?.strategy, pos };
        const repaired = repairMangledDate(current);
        if (repaired && parseDate(repaired)) return { value: repaired, strategy: meta?.strategy, pos };
      }
      for (const candidate of candidatePool) {
        if (claimedFromPool.has(candidate.text)) continue;
        if (parseDate(candidate.text)) {
          claimedFromPool.add(candidate.text);
          return { value: candidate.text, strategy: 'pattern-anchor-fallback', pos: candidate.pos };
        }
        const repaired = repairMangledDate(candidate.text);
        if (repaired && parseDate(repaired)) {
          claimedFromPool.add(candidate.text);
          return { value: repaired, strategy: 'pattern-anchor-fallback', pos: candidate.pos };
        }
      }
      // No label-associated value parsed (or repaired) as a date, and no
      // unclaimed pool candidate does either — better to leave the field
      // unset (surfaces as "not provided" in the engine) than to trust a
      // value that doesn't look like a date at all (see branch brief's
      // NO-GUESS policy, item 3).
      return { value: undefined, strategy: undefined };
    }

    const dobResolved = resolveDateField('dob');
    const writtenResolved = resolveDateField('written');
    const dob = dobResolved.value;
    const written = writtenResolved.value;

    // ---- Diagnostics helpers (item 4) ----
    function labelDiag(key: LabelKey): FieldDiagnostic['label'] {
      const p = labelPositions.find((_, i) => labelOrder[i] === key);
      return p ? { text: p.text, x: p.x, y: p.y } : undefined;
    }
    function pushResolved(field: string, key: LabelKey, text: string, override?: FieldDiagnostic['strategy']) {
      const meta = resolutionMeta[key];
      const pos = meta?.words[0];
      diagnostics.push({
        field,
        status: 'resolved',
        strategy: override ?? meta?.strategy ?? 'pattern-anchor',
        label: labelDiag(key),
        value: pos ? { text, x: pos.x, y: pos.y } : { text, x: 0, y: 0 }
      });
    }
    function pushMiss(field: string, key: LabelKey | null, reason: string) {
      diagnostics.push({ field, status: 'miss', label: key ? labelDiag(key) : undefined, reason });
    }
    function pushPatternResolved(field: string, word: OcrWord | undefined, text: string) {
      diagnostics.push({
        field,
        status: 'resolved',
        strategy: 'pattern-anchor',
        value: word ? { text, x: word.x, y: word.y } : { text, x: 0, y: 0 }
      });
    }

    // ---- Assemble the PrescriptionRecord ----
    if (raw.patient) {
      record.patientName = raw.patient;
      pushResolved('patientName', 'patient', raw.patient);
    } else {
      pushMiss('patientName', labelPositions.some((_, i) => labelOrder[i] === 'patient') ? 'patient' : null, 'label-not-found');
    }

    if (dob) {
      record.patientDOB = dob;
      pushResolved('patientDOB', 'dob', dob, dobResolved.strategy);
    } else {
      pushMiss('patientDOB', 'dob', raw.dob ? 'validation-failed:date-shape' : 'no-value-paired');
    }

    if (raw.address) {
      record.patientAddress = parseAddressBlob(raw.address);
      pushResolved('patientAddress', 'address', raw.address);
    } else {
      pushMiss('patientAddress', 'address', 'no-value-paired');
    }

    if (written) {
      record.dateWritten = written;
      pushResolved('dateWritten', 'written', written, writtenResolved.strategy);
    } else {
      pushMiss('dateWritten', 'written', raw.written ? 'validation-failed:date-shape' : 'no-value-paired');
    }

    // "O." + a following digit is OCR splitting "0.X" (e.g. "0.5 ML") into
    // two words around the period — repaired here (space removed, "O."->
    // "0.") ONLY for this exact literal shape. Deliberately narrow: sig is
    // free text, so no broader digit-repair (repairDigits) is applied to
    // it — that's reserved for numeric/date fields where majority-digit
    // heuristics are meaningful.
    if (raw.directions) {
      record.sig = raw.directions.replace(/\bO\.\s+(?=\d)/g, '0.');
      pushResolved('sig', 'directions', record.sig);
    } else {
      pushMiss('sig', 'directions', 'no-value-paired');
    }
    if (raw.note) {
      // NOTE: no `notes` field exists on PrescriptionRecord (types.ts) —
      // mirrors the same, already-documented gap in the retired C#
      // OcrEscriptParser ("Free-text Notes are NOT extracted"). Parsed
      // but intentionally dropped rather than invented a new field; see
      // branch report.
    }
    if (raw.substitutions !== undefined) {
      const subs = parseSubstitutionsNotAllowed(raw.substitutions);
      if (subs !== undefined) {
        record.substitutionsNotAllowed = subs;
        pushResolved('substitutionsNotAllowed', 'substitutions', raw.substitutions);
      } else {
        pushMiss('substitutionsNotAllowed', 'substitutions', 'ambiguous-value');
      }
    } else {
      pushMiss('substitutionsNotAllowed', 'substitutions', 'no-value-paired');
    }
    if (raw.quantity) {
      const q = parseQuantity(raw.quantity);
      if (q.quantity) {
        record.quantity = q.quantity;
        if (q.quantityUnit) record.quantityUnit = q.quantityUnit;
        pushResolved('quantity', 'quantity', q.quantity);
      } else {
        pushMiss('quantity', 'quantity', 'validation-failed:not-numeric');
      }
    } else {
      pushMiss('quantity', 'quantity', 'no-value-paired');
    }
    if (raw.refills) {
      const refills = parseRefills(raw.refills);
      if (refills) {
        record.refills = refills;
        // Change 3: "Total fills: N" (seen on responded refill-request
        // e-scripts) counts the initial fill plus refills, so the
        // refills value is compared as N-1 — see
        // PrescriptionRecord.refillsFromTotalFills (types.ts) and
        // compareRefills (quantity/index.ts). Only set when the label
        // that actually resolved 'refills' was the 'totalfills' variant
        // — the ordinary 'refills'/'refillsauthorized'/'refillsremaining'
        // variants never subtract.
        const refillsLabelIdx = labelOrder.findIndex((k, i) => k === 'refills' && labelCanonicals[i] === 'totalfills');
        if (refillsLabelIdx !== -1) record.refillsFromTotalFills = true;
        pushResolved('refills', 'refills', refills);
      } else {
        pushMiss('refills', 'refills', 'validation-failed:not-numeric');
      }
    } else {
      pushMiss('refills', 'refills', 'no-value-paired');
    }

    const prescriber: Prescriber = {};
    if (raw.prescriber) {
      prescriber.name = raw.prescriber;
      pushResolved('prescriber.name', 'prescriber', raw.prescriber);
    } else {
      pushMiss('prescriber.name', 'prescriber', 'no-value-paired');
    }
    if (raw.phone) {
      if (isPhoneShaped(raw.phone)) {
        prescriber.phone = raw.phone;
        pushResolved('prescriber.phone', 'phone', raw.phone);
      } else {
        pushMiss('prescriber.phone', 'phone', 'validation-failed:phone-shape');
      }
    } else {
      pushMiss('prescriber.phone', 'phone', 'no-value-paired');
    }
    if (raw.location) {
      prescriber.address = parseAddressBlob(raw.location);
      pushResolved('prescriber.address', 'location', raw.location);
    } else {
      pushMiss('prescriber.address', 'location', 'no-value-paired');
    }
    if (npi) {
      prescriber.npi = npi;
      pushPatternResolved('prescriber.npi', npiWord ?? undefined, npi);
    } else {
      pushMiss('prescriber.npi', null, 'not-found-in-capture');
    }
    const hasPrescriberData =
      prescriber.name !== undefined ||
      prescriber.phone !== undefined ||
      prescriber.address !== undefined ||
      prescriber.npi !== undefined;
    if (hasPrescriberData) record.prescriber = prescriber;

    const drug: DrugDescriptor = {};
    if (raw.medication) {
      drug.name = raw.medication;
      pushResolved('drug.name', 'medication', raw.medication);
    } else {
      pushMiss('drug.name', 'medication', 'no-value-paired');
    }
    if (ndc) {
      drug.ndc = ndc;
      pushPatternResolved('drug.ndc', ndcWord ?? undefined, ndc);
    } else {
      pushMiss('drug.ndc', null, 'not-found-in-capture');
    }
    const hasDrugData = drug.name !== undefined || drug.ndc !== undefined;
    if (hasDrugData) record.drug = drug;

    appendOcrDiagnosticsLog(diagnostics);
  } catch {
    // Never throw — a parsing bug degrades to "field not found", not a
    // crashed overlay refresh. See class doc.
  }

  return record;
}
