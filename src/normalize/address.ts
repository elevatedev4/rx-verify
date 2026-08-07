/**
 * Address normalization + comparison.
 *
 * Verdict philosophy: address alone is never RED — patients move, and a
 * stale address is not a contradiction that blocks dispensing. A full
 * mismatch is YELLOW with guidance to verify identity via DOB instead.
 *  - normalized-equal (all components) = GREEN
 *  - unit-only difference, or source missing = YELLOW
 *  - different street/city/zip = YELLOW address_differs
 */

import type { Address } from '../types.js';

export type AddressCompareStatus = 'green' | 'yellow';

export interface AddressCompareResult {
  status: AddressCompareStatus;
  reasonCode: string;
  explanation: string;
}

/**
 * USPS Pub 28 common street-suffix abbreviations (subset). Exported so
 * src/ocr/parseEscriptOcr.ts's address parsing can reuse this SAME table
 * (branch brief defect #7b — "reuse the existing suffix table") rather
 * than maintaining a second, driftable copy of the suffix word list.
 */
export const STREET_SUFFIXES: Record<string, string> = {
  street: 'st', st: 'st',
  avenue: 'ave', ave: 'ave', av: 'ave',
  road: 'rd', rd: 'rd',
  drive: 'dr', dr: 'dr',
  lane: 'ln', ln: 'ln',
  boulevard: 'blvd', blvd: 'blvd',
  court: 'ct', ct: 'ct',
  circle: 'cir', cir: 'cir',
  highway: 'hwy', hwy: 'hwy',
  parkway: 'pkwy', pkwy: 'pkwy',
  place: 'pl', pl: 'pl',
  terrace: 'ter', ter: 'ter',
  trail: 'trl', trl: 'trl',
  way: 'way',
  square: 'sq', sq: 'sq',
  loop: 'loop'
};

const DIRECTIONALS: Record<string, string> = {
  north: 'n', n: 'n',
  south: 's', s: 's',
  east: 'e', e: 'e',
  west: 'w', w: 'w',
  northeast: 'ne', ne: 'ne',
  northwest: 'nw', nw: 'nw',
  southeast: 'se', se: 'se',
  southwest: 'sw', sw: 'sw'
};

const UNIT_DESIGNATORS = ['apt', 'apartment', 'unit', 'ste', 'suite', '#'];

function foldCase(s: string): string {
  return s.toLowerCase().replace(/[.,]/g, '').replace(/\s+/g, ' ').trim();
}

/**
 * Unit-designator words that OCR sometimes glues directly onto the
 * following unit value with no space at all — e.g. "suite205" (live-test
 * bug: source "...suite205..." vs entered "...Suite 205..." read as a
 * false address_differs because "suite205" never matched the UNIT_
 * DESIGNATORS token check below, which requires the designator to be its
 * OWN token). Split BEFORE any other normalization runs (suffix/
 * directional/unit) so the existing UNIT_DESIGNATORS handling in
 * normalizeStreetLine sees "suite" and "205" as separate tokens exactly
 * like the already-correct "Suite 205" side does. Deliberately scoped to
 * just these known designator words (not a blind "any letter run
 * followed by any digit run" split) — splitting only where we already
 * know a unit designator is expected keeps this from mangling a street
 * NAME that happens to end in digits for an unrelated reason.
 */
const GLUED_UNIT_RE = /^(apartment|apt|suite|ste|unit)(\d.*)$/;

function splitGluedUnitTokens(s: string): string {
  return s
    .split(' ')
    .map((tok) => {
      const m = GLUED_UNIT_RE.exec(tok);
      return m ? `${m[1]} ${m[2]}` : tok;
    })
    .join(' ');
}

/** Standard Levenshtein edit distance, small-string DP (address tokens are short). */
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

/**
 * INVARIANT: this comparator may only ever fail toward MORE yellow, never
 * produce a false GREEN on two addresses that are actually different (the
 * same stated philosophy as src/drug/index.ts). Every tolerance below is a
 * single, narrow, DOCUMENTED exception to that invariant, not a general
 * fuzzy-match — see the review history in this function's own git blame
 * (round 1 shipped a plain <=1-edit-distance tolerance with no length
 * constraint, which a review then caught actually producing false GREENs
 * on real address pairs like "100 Meadow Ln" vs "100 Meadows Ln" and "400
 * Wilson St" vs "400 Wilton St" — an insertion/deletion is a much more
 * common way for two DIFFERENT street names to collide than a same-length
 * substitution is).
 *
 * True if two ALREADY street-suffix/directional-normalized street tokens
 * should be treated as the same word. Exact match always passes. Beyond
 * that, tolerance is intentionally narrow and asymmetric by token shape:
 *  - Any token that is purely digits (house numbers, unit numbers that
 *    slipped through as part of base, ZIP-shaped fragments) must match
 *    EXACTLY — never fuzzed. This is the safety bound from the verdict
 *    philosophy stated elsewhere in this file (never a false GREEN on an
 *    identity-critical value): a single-character edit-distance pass on
 *    "4930" vs "4931" would silently treat two different house numbers
 *    as the same address.
 *  - Alphabetic-only tokens of length >=5, THE SAME LENGTH AS EACH OTHER,
 *    tolerate a Levenshtein distance of exactly 1 — which, for two
 *    EQUAL-length strings, can only ever be a single-character
 *    SUBSTITUTION (an edit distance of 1 between equal-length strings is
 *    never reachable via an insert or delete alone — either one changes
 *    the length). This covers the real, reported single-character OCR
 *    misread (live-test bug: source "overtand" vs entered "Overland" —
 *    same length, one substitution) while deliberately EXCLUDING the
 *    insertion/deletion class entirely: "meadow"(6) vs "meadows"(7) or
 *    "parkway"(7) vs "parkways"(8) now fail the length check up front and
 *    fall through to exact-match, because those are a much more common
 *    and much more dangerous way for two genuinely different street names
 *    to look almost identical than a same-length substitution is.
 *  - RESIDUAL, KNOWINGLY ACCEPTED RISK: a substitution-only tolerance
 *    still can't distinguish an OCR misread from an equal-length, one-
 *    letter-different DIFFERENT street name — e.g. "Wilson" vs "Wilton"
 *    (both real, plausible street names) still matches under this rule;
 *    see the explicit test asserting that behavior in
 *    tests/normalize-address.test.ts for the reasoning: address is a
 *    yellow-tier, "patients move"/never-blocks-dispensing field with
 *    patient identity actually carried by name+DOB elsewhere in the
 *    engine, and the alternative (no street-name tolerance at all)
 *    reproduces the exact daily false-yellow alarm fatigue this fix
 *    exists to fix (the live-reported "overtand"/"Overland" bug). Zero
 *    tolerance trades a rare false GREEN for a routine false YELLOW on
 *    a field the product treats as advisory, not a hard identity check.
 *  - Anything else (mixed alnum, short tokens, differing lengths,
 *    differing shapes) falls back to exact match.
 */
function streetTokensMatch(a: string, b: string): boolean {
  if (a === b) return true;
  const isNumeric = (t: string) => /^\d+$/.test(t);
  if (isNumeric(a) || isNumeric(b)) return false;
  const isAlpha = (t: string) => /^[a-z]+$/.test(t);
  if (isAlpha(a) && isAlpha(b) && a.length >= 5 && a.length === b.length) {
    return levenshtein(a, b) <= 1;
  }
  return false;
}

/**
 * True if two already-normalized street CORE strings (house number +
 * street name, unit/suffix already stripped out by normalizeStreetLine)
 * differ. Word-count MUST match to attempt token-wise fuzzy comparison —
 * a differing word count means the two strings aren't lined up token-for-
 * token in any safe, non-guessing way, so this falls back to a plain
 * whole-string compare (same behavior as before this fix) rather than
 * risk mis-aligning tokens and fuzz-matching the wrong pair.
 */
function streetBaseDiffers(a: string, b: string): boolean {
  if (a === b) return false;
  const aTokens = a.split(' ').filter(Boolean);
  const bTokens = b.split(' ').filter(Boolean);
  if (aTokens.length !== bTokens.length) return true;
  for (let i = 0; i < aTokens.length; i++) {
    if (!streetTokensMatch(aTokens[i] as string, bTokens[i] as string)) return true;
  }
  return false;
}

/** Set of normalized (abbreviated) street-suffix tokens, e.g. "st", "ave", "rd". Used to split a trailing suffix token off the street CORE so a suffix present on only one side (or missing entirely) doesn't fail the comparison — see NormalizedStreet.suffix. */
const SUFFIX_ABBREVIATIONS = new Set(Object.values(STREET_SUFFIXES));

/** Set of normalized directional abbreviations ("n", "ne", ...). Used by isGarbageSuffixCandidate so a legitimate leading/trailing directional is never mistaken for OCR noise. */
const DIRECTIONAL_ABBREVIATIONS = new Set(Object.values(DIRECTIONALS));

/**
 * True for a short (1-2 char), alphabetic token that ISN'T any known
 * real address token (not a recognized street suffix, not a directional
 * abbreviation). Used only for the narrow "garbage trailing token where
 * a real suffix should be" case (live-test bug: source street suffix
 * "Dr" OCR-misread as the single character "m") — see
 * NormalizedStreet.trailingToken and compareAddresses' use of it. A
 * token this short that ISN'T a recognized suffix/directional has no
 * other plausible reading as real street-name text (real street-name
 * words this short essentially don't occur), so treating it as noise
 * carries negligible risk of masking an actually different street.
 */
function isGarbageSuffixCandidate(token: string): boolean {
  return /^[a-z]{1,2}$/.test(token) && !SUFFIX_ABBREVIATIONS.has(token) && !DIRECTIONAL_ABBREVIATIONS.has(token);
}

interface NormalizedStreet {
  /** Street line with unit AND trailing suffix stripped out, tokens normalized. */
  base: string;
  /** Trailing street-type suffix (already normalized to its abbreviation, e.g. "st"), or null if the line didn't end in a recognized one. */
  suffix: string | null;
  unit: string | null;
  /**
   * The line's actual trailing token, but ONLY when `suffix` is null
   * (nothing recognized there) — the candidate garbage token itself, for
   * compareAddresses to evaluate via isGarbageSuffixCandidate against
   * the OTHER side's suffix. Null whenever `suffix` is non-null, or the
   * line is too short to have a distinct trailing token.
   */
  trailingToken: string | null;
  /** `base` with its own trailing token ALSO removed — only set alongside `trailingToken` (i.e. only meaningful when `suffix` is null). Lets compareAddresses drop the trailing token as OCR noise without re-deriving it from `base` by string surgery. */
  baseWithoutTrailingToken: string | null;
}

/** Split a raw street line into base + unit, and normalize tokens. */
function normalizeStreetLine(raw: string): NormalizedStreet {
  let s = splitGluedUnitTokens(foldCase(raw));

  // Extract "#123" style unit anywhere in the string.
  let unit: string | null = null;
  const hashMatch = /#\s*(\S+)/.exec(s);
  if (hashMatch) {
    unit = hashMatch[1] ?? null;
    s = s.replace(hashMatch[0], '').trim();
  }

  const tokens = s.split(' ').filter(Boolean);
  const outTokens: string[] = [];
  for (let i = 0; i < tokens.length; i++) {
    const tok = tokens[i] ?? '';
    if (unit === null && UNIT_DESIGNATORS.includes(tok) && i + 1 < tokens.length) {
      unit = tokens[i + 1] ?? null;
      i++; // consume the unit value too
      continue;
    }
    outTokens.push(tok);
  }

  const normalized = outTokens.map((tok) => {
    if (STREET_SUFFIXES[tok]) return STREET_SUFFIXES[tok] as string;
    if (DIRECTIONALS[tok]) return DIRECTIONALS[tok] as string;
    return tok;
  });

  // Split a trailing street-type suffix (e.g. "st", "ave") off the core
  // street text. This is deliberately separate from `base` — see
  // compareAddresses, which only treats differing suffixes as a real
  // mismatch when BOTH sides actually state one; a suffix missing
  // entirely on one side (e.g. entered "330 Sycamore" vs source "330
  // Sycamore St" — a real live-test false-mismatch, Will's bug report)
  // is just an incomplete entry, not a different street.
  let suffix: string | null = null;
  let core = normalized;
  let trailingToken: string | null = null;
  let baseWithoutTrailingToken: string | null = null;
  const last = normalized[normalized.length - 1];
  if (normalized.length > 1 && last !== undefined && SUFFIX_ABBREVIATIONS.has(last)) {
    suffix = last;
    core = normalized.slice(0, -1);
  } else if (normalized.length > 1 && last !== undefined) {
    trailingToken = last;
    baseWithoutTrailingToken = normalized.slice(0, -1).join(' ');
  }

  return {
    base: core.join(' '),
    suffix,
    unit: unit ? unit.toLowerCase() : null,
    trailingToken,
    baseWithoutTrailingToken
  };
}

/**
 * Normalize a unit VALUE that arrived via the dedicated Address.unit
 * field (as opposed to embedded inline in the street line, which
 * normalizeStreetLine already strips/normalizes on its own). A direct
 * Address.unit value may still carry its own designator word ("Ste C",
 * "Apt 4") rather than just the bare value ("C", "4") — strip that
 * leading designator here too, so a unit stated via the dedicated field
 * on one side compares equal to the same unit stripped out of an inline
 * street string on the other side.
 */
function normalizeUnitValue(raw: string): string {
  const folded = foldCase(raw);
  const tokens = folded.split(' ').filter(Boolean);
  if (tokens.length > 1 && UNIT_DESIGNATORS.includes(tokens[0] ?? '')) {
    return tokens.slice(1).join(' ');
  }
  return folded;
}

function normalizeCity(raw: string): string {
  return foldCase(raw);
}

function normalizeState(raw: string): string {
  return foldCase(raw).replace(/\s+/g, '');
}

function normalizeZip(raw: string): string {
  // Compare on the 5-digit base; ZIP+4 vs ZIP5 is not treated as a diff.
  const digits = raw.replace(/[^0-9]/g, '');
  return digits.slice(0, 5);
}

/**
 * Parse a SINGLE freeform address line (the entered/PioneerRx shape —
 * uxPatientAddress/uxWrittenByAddress are one combined string with no
 * separate city/state/zip controls at all, confirmed in both real UIA
 * dumps, e.g. a synthetic example in that shape: "100 Fake St
 * Testville, KS") into the SAME {street, city, state, zip} shape the
 * source (escript) side already provides as separate components.
 *
 * This is the actual fix for the "freeform vs component" case: the
 * previous version of this file compared the two shapes as one long
 * token string (a whole-line prefix match) instead of extracting real
 * city/state/zip components out of the freeform text. That whole-string
 * approach was asymmetric in a way that could silently produce a false
 * MISMATCH on a genuinely identical address: the structured (source)
 * path already strips a unit designator (e.g. "Ste C" -> unit "c") out
 * of the street text via normalizeStreetLine before comparing, but the
 * old freeform tokenizer did not — so an address with a suite/apartment
 * entered inline on the freeform side (a real, dump-confirmed shape —
 * PioneerRx's prescriber-address field routinely includes a suite
 * inline) would misalign every token after the unit and read as a
 * totally different address. Parsing the freeform line into real
 * components FIRST, then running it through the exact same street/unit
 * normalization as the structured side, fixes that asymmetry: both
 * sides get unit-stripped identically before comparison.
 *
 * The trailing "<city>, <ST> [ZIP]" shape is the one confirmed by both
 * real dumps. City is taken as the single trailing word before the
 * comma (every confirmed real example is a one-word city name); a
 * genuinely multi-word city would fall back to being included as part
 * of the parsed "street" text, which just means that one component
 * isn't split out — it does not cause a false match, only a slightly
 * less specific comparison for that rare case.
 */
function parseFreeformAddress(raw: string): { street: string; city: string | null; state: string | null; zip: string | null } {
  const trimmed = raw.trim();
  // Prefer the literal-comma form first (unambiguous). Live-test bug:
  // the entered freeform line sometimes omits the comma before the state
  // entirely ("...Testville KS") — the source-side parser (ADDRESS_RE in
  // src/ocr/parseEscriptOcr.ts) already tolerates a comma-OR-whitespace
  // separator here, so a comma-less entered line fell through to the
  // no-match branch below and dumped the whole remaining line (city AND
  // state included) into `street` undifferentiated, misaligning the
  // street-core token count against the source's cleanly split
  // components. Falling back to a bare-whitespace separator closes that
  // gap — BUT only when the candidate 2-letter "state" token isn't
  // itself a recognized street-type suffix (SUFFIX_ABBREVIATIONS
  // overlaps real state codes closely enough — "Ct" is both Court and
  // Connecticut — that a comma-less, city-less street ending in its own
  // suffix, e.g. "330 Sycamore St", would otherwise be misread as a
  // city-less state). That narrower case is left exactly as before
  // (whole line treated as street) rather than risk a wrong split.
  let m = /^(.*?),\s*([A-Za-z]{2})\s*(\d{5}(?:-\d{4})?)?\s*$/.exec(trimmed);
  if (!m) {
    const loose = /^(.*?)\s+([A-Za-z]{2})\s*(\d{5}(?:-\d{4})?)?\s*$/.exec(trimmed);
    if (loose && !SUFFIX_ABBREVIATIONS.has((loose[2] ?? '').toLowerCase())) {
      m = loose;
    }
  }
  if (!m) {
    // No recognizable ", ST [ZIP]" tail at all — nothing to split out;
    // treat the whole line as street and leave city/state/zip unknown
    // rather than guessing.
    return { street: trimmed, city: null, state: null, zip: null };
  }

  const beforeComma = (m[1] ?? '').trim();
  const state = (m[2] ?? '').trim() || null;
  const zip = m[3] ? m[3].trim() : null;

  const tokens = beforeComma.split(/\s+/).filter(Boolean);
  if (tokens.length < 2) {
    return { street: beforeComma, city: null, state, zip };
  }

  const city = tokens[tokens.length - 1] ?? null;
  const street = tokens.slice(0, -1).join(' ');
  return { street, city, state, zip };
}

/** Resolved {street, city, state, zip} for one side, regardless of whether it arrived as separate components or one freeform line. */
interface AddressComponents {
  street: string;
  city: string;
  state: string;
  zip: string;
}

/**
 * Normalize an Address (from either shape) down to its raw component
 * strings, parsing a freeform single line into components first if it
 * doesn't already have separate city/state/zip. See parseFreeformAddress
 * for why this must happen BEFORE normalization/comparison rather than
 * comparing the two shapes as whole strings.
 */
function resolveComponents(addr: Address): AddressComponents {
  const hasSeparateComponents = Boolean(addr.city || addr.state || addr.zip);
  if (hasSeparateComponents) {
    return {
      street: addr.street ?? '',
      city: addr.city ?? '',
      state: addr.state ?? '',
      zip: addr.zip ?? ''
    };
  }

  const parsed = parseFreeformAddress(addr.street ?? '');
  return {
    street: parsed.street,
    city: parsed.city ?? '',
    state: parsed.state ?? '',
    zip: parsed.zip ?? ''
  };
}

/**
 * True if both sides state a value for this component and, after
 * normalization, it differs. A component that's blank/unstated on
 * EITHER side is not treated as a mismatch — the entered freeform line
 * routinely omits the ZIP entirely (and sometimes state/city, if the
 * line doesn't match the expected "..., ST ZIP" shape), and that's a
 * known/expected gap in what PioneerRx exposes, not a real discrepancy.
 */
function componentDiffers(a: string, b: string): boolean {
  return a !== '' && b !== '' && a !== b;
}

/**
 * Like componentDiffers, but ASYMMETRIC — used for CITY (branch brief
 * defect #7c, safety bound (ii)) and, via sourceAbsentIsGap below, for
 * STREET's base too (round-2 review fold #2). A blank on the ENTERED
 * side keeps the exact same tolerance as componentDiffers (the entered
 * freeform line commonly omits trailing components — see
 * componentDiffers' own doc). A blank on the SOURCE side, however, is
 * treated as a genuine gap whenever the entered side DOES state a value:
 * a source that never even reaches this component ("too little to
 * verify") is not the same thing as a confirmed match — unlike state/
 * ZIP, which get an explicit, EXPLAINED leniency further down
 * (compareAddresses' own "confirmed street+city, source just didn't
 * reach state/ZIP" branch) specifically because street+city being
 * independently confirmed is what makes THAT leniency safe. Street and
 * city have no equivalent "something else confirms it" signal, so their
 * own absence on the source is never waved through.
 */
function cityMissingOrDiffers(sourceCity: string, enteredCity: string): boolean {
  return sourceAbsentIsGap(sourceCity, enteredCity) || componentDiffers(sourceCity, enteredCity);
}

/**
 * True when `enteredVal` states something but `sourceVal` is blank — see
 * cityMissingOrDiffers' doc for the full rationale. Reused directly (not
 * a strict equality check) by streetDiffers below, which layers its own
 * fuzzy streetBaseDiffers comparison on top for the "both present" case
 * — this helper only covers the "source never even stated it" gap.
 */
function sourceAbsentIsGap(sourceVal: string, enteredVal: string): boolean {
  return enteredVal !== '' && sourceVal === '';
}

export function compareAddresses(
  sourceRaw: Address | null | undefined,
  enteredRaw: Address | null | undefined
): AddressCompareResult {
  const sourceEmpty = !sourceRaw || Object.values(sourceRaw).every((v) => !v || !String(v).trim());
  const enteredEmpty = !enteredRaw || Object.values(enteredRaw).every((v) => !v || !String(v).trim());

  if (sourceEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription did not provide a patient address to compare.'
    };
  }
  if (enteredEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'No address was entered in PioneerRx to compare against the source.'
    };
  }

  const src = sourceRaw as Address;
  const ent = enteredRaw as Address;

  // COMPONENT-LEVEL COMPARISON, always — whether a side arrived as
  // separate fields (source/escript) or one freeform line (entered/
  // PioneerRx), it's resolved to the same {street, city, state, zip}
  // shape first (see resolveComponents/parseFreeformAddress), then
  // house-number+street-name, city, state, and zip are compared as
  // distinct components rather than one fuzzy whole-string match. This
  // is deliberately stricter about WHICH component disagrees (so a
  // genuine city/state/zip mismatch can't be masked by a coincidental
  // token-position alignment) while still tolerating: (a) street-suffix/
  // directional abbreviations ("St" vs "Street", "N" vs "North" — see
  // normalizeStreetLine), and (b) a component the entered freeform line
  // simply doesn't state at all (componentDiffers only flags an actual
  // stated disagreement, never a blank vs a value).
  //
  // Street NAME text gets a NARROW edit-distance tolerance too (live-test
  // bug: source "overtand" vs entered "Overland" — a single-character OCR
  // misread — read as address_differs): alphabetic street-core tokens of
  // length >=5, THE SAME LENGTH, match if they're <=1 Levenshtein edit
  // apart — which for equal-length strings can only be a single-character
  // SUBSTITUTION, never an insertion/deletion (see streetTokensMatch/
  // streetBaseDiffers for the full invariant + accepted-risk writeup — an
  // earlier, unconstrained (no length check) version of this tolerance
  // was caught by review producing a false GREEN on "Meadow"/"Meadows";
  // requiring equal length closes that insertion/deletion class entirely.
  // A same-length, one-substitution collision like "Wilson"/"Wilton"
  // remains a knowingly accepted residual risk — see streetTokensMatch).
  // Per this engine's stated philosophy elsewhere (see src/drug/index.ts's
  // "can only ever fail toward MORE yellow, never a false green"), this
  // tolerance is DELIBERATELY narrow and asymmetric: numeric tokens (house
  // numbers, unit numbers, ZIPs already handled below) and state codes
  // are never fuzzed — only alphabetic street-name words get the
  // 1-substitution allowance, so a permissive match can blur
  // "Overland"/"overtand" but can never treat two different house
  // numbers, unit numbers, or states as the
  // same one.
  const srcComponents = resolveComponents(src);
  const entComponents = resolveComponents(ent);
  const srcStreet = normalizeStreetLine(srcComponents.street);
  const entStreet = normalizeStreetLine(entComponents.street);

  const srcCity = normalizeCity(srcComponents.city);
  const entCity = normalizeCity(entComponents.city);
  const srcState = normalizeState(srcComponents.state);
  const entState = normalizeState(entComponents.state);
  const srcZip = srcComponents.zip ? normalizeZip(srcComponents.zip) : '';
  const entZip = entComponents.zip ? normalizeZip(entComponents.zip) : '';
  const srcUnit = srcStreet.unit ?? (src.unit ? normalizeUnitValue(src.unit) : null);
  const entUnit = entStreet.unit ?? (ent.unit ? normalizeUnitValue(ent.unit) : null);

  // Street CORE (number + name, suffix/directional-normalized, suffix
  // split out) must agree. The trailing suffix itself ("st"/"ave"/...)
  // is only checked when BOTH sides actually state one — a suffix
  // missing on just one side (e.g. "330 Sycamore" vs "330 Sycamore St")
  // is an incomplete entry, not evidence of a different street, and
  // must not fail the match. Per the "address alone is never RED"
  // stated philosophy, if the suffix genuinely differs on both sides
  // (e.g. "St" vs "Ave") it's still a real signal worth a yellow.
  //
  // ROUND-2 REVIEW FOLD #2: the base-vs-base comparison used to be fully
  // symmetric (a blank base on EITHER side skipped it) — same class of
  // gap as cityMissingOrDiffers originally fixed for city, just not yet
  // applied to street: a source with NO street at all (e.g. only a
  // `city` field, nothing else) compared against a fully-populated
  // entered address could fall through every differs-check and land
  // GREEN via the generic exact_match fallthrough below, having
  // confirmed nothing. sourceAbsentIsGap closes that the same way city's
  // already does: entered-blank stays tolerated (unchanged), but
  // source-blank-while-entered-states-one is now a genuine gap.
  //
  // BUG 2 (round 3, live-test): a GARBAGE trailing token where a real
  // suffix should be — source street suffix "Dr" OCR-misread as the
  // single character "m" — must not fail the match either, same
  // rationale as a suffix missing outright. Only fires on the side that
  // has NO recognized suffix at all (suffix === null) when its own
  // trailing token is short/unrecognized/non-directional
  // (isGarbageSuffixCandidate) AND the OTHER side DOES state a real,
  // recognized suffix — i.e. exactly the shape of "one side's suffix got
  // eaten by OCR", never a case where neither side has a real suffix
  // (that's ordinary core-text comparison, unchanged) or both sides have
  // one (handled, unchanged, by the differing-suffix check below).
  const srcBaseForCompare =
    srcStreet.suffix === null &&
    entStreet.suffix !== null &&
    srcStreet.trailingToken !== null &&
    srcStreet.baseWithoutTrailingToken !== null &&
    isGarbageSuffixCandidate(srcStreet.trailingToken)
      ? srcStreet.baseWithoutTrailingToken
      : srcStreet.base;
  const entBaseForCompare =
    entStreet.suffix === null &&
    srcStreet.suffix !== null &&
    entStreet.trailingToken !== null &&
    entStreet.baseWithoutTrailingToken !== null &&
    isGarbageSuffixCandidate(entStreet.trailingToken)
      ? entStreet.baseWithoutTrailingToken
      : entStreet.base;
  const streetDiffers =
    sourceAbsentIsGap(srcBaseForCompare, entBaseForCompare) ||
    (srcBaseForCompare !== '' && entBaseForCompare !== '' && streetBaseDiffers(srcBaseForCompare, entBaseForCompare)) ||
    (srcStreet.suffix !== null && entStreet.suffix !== null && srcStreet.suffix !== entStreet.suffix);
  const cityDiffers = cityMissingOrDiffers(srcCity, entCity);
  const stateDiffers = componentDiffers(srcState, entState);
  const zipDiffers = componentDiffers(srcZip, entZip);

  // DEFECT #7c (live-test bug: an OCR-read address that stops at the
  // city — "808 E 1250 Road Lawrence", no state/ZIP recognized in that
  // capture at all — compared against a fully-populated entered address
  // and landed address_differs). OCR routinely drops the tail of an
  // address line; that is not evidence the address is actually
  // different, only that the SOURCE couldn't see that far. When state
  // and/or ZIP are simply ABSENT on the source — never present-and-
  // different, see safety bound (i) below — AND street+city ARE present
  // on the source and actively confirmed matching the entered value,
  // treat it as GREEN with an explanation that says exactly what was (and
  // wasn't) compared, rather than silently folding into the generic
  // "matches after normalization" message below. This is scoped
  // narrowly: address is a yellow-tier field that never blocks
  // dispensing, and patient identity is carried by name+DOB elsewhere in
  // this engine, not by address — so a confirmed street+city match is
  // enough to clear this field even when state/ZIP were never legible.
  //
  // SAFETY BOUNDS:
  //  (i) present-but-different state/ZIP still flags normally — this
  //      branch requires `!stateDiffers && !zipDiffers` explicitly, so a
  //      source that DOES state a (different) state/ZIP falls straight
  //      through to the ordinary address_differs handling below, never
  //      this leniency.
  //  (ii) source missing city too (street-only) does NOT qualify —
  //      cityMissingOrDiffers (unlike the general componentDiffers used
  //      for state/ZIP) treats a source-blank city as a genuine gap
  //      whenever the entered side states one, so cityDiffers is already
  //      true in that case and this branch's `!cityDiffers` guard
  //      excludes it — see cityMissingOrDiffers' own doc.
  //  (iii) this leniency applies ONLY to state/ZIP absence — street and
  //      city must BOTH be present and actively confirmed matching
  //      (srcStreet/entStreet non-blank and !streetDiffers; srcCity/
  //      entCity non-blank and !cityDiffers); if either is blank or
  //      differs, this branch is skipped and falls through unchanged.
  const sourceMissingStateOrZip = srcState === '' || srcZip === '';
  const streetAndCityConfirmed =
    srcStreet.base !== '' &&
    entStreet.base !== '' &&
    !streetDiffers &&
    srcCity !== '' &&
    entCity !== '' &&
    !cityDiffers;
  if (sourceMissingStateOrZip && !stateDiffers && !zipDiffers && streetAndCityConfirmed) {
    const missingParts: string[] = [];
    if (srcState === '') missingParts.push('state');
    if (srcZip === '') missingParts.push('ZIP');
    return {
      status: 'green',
      reasonCode: 'exact_match_partial_source',
      explanation: `Street and city match; source did not provide ${missingParts.join('/')}.`
    };
  }

  if (streetDiffers || cityDiffers || stateDiffers || zipDiffers) {
    return {
      status: 'yellow',
      reasonCode: 'address_differs',
      explanation:
        'Street, city, state, or ZIP differs from the source after normalization. Address alone does not block dispensing (patients move) — verify identity via DOB.'
    };
  }

  if (srcUnit !== entUnit) {
    // Includes "one side states a unit, the other doesn't mention one at
    // all" (missing unit on one side only) — per the owner's requirement
    // that's a soft signal worth a glance, never a hard mismatch. It's
    // downgraded to its own unit_differs reason code (not address_differs)
    // precisely so it reads as "everything else matches, just double-check
    // the suite/apt" rather than "this looks like a different address".
    return {
      status: 'yellow',
      reasonCode: 'unit_differs',
      explanation: `Street, city, state, and ZIP match; unit differs ("${srcUnit ?? 'none'}" vs "${entUnit ?? 'none'}").`
    };
  }

  return {
    status: 'green',
    reasonCode: 'exact_match',
    explanation: 'Address matches after normalization (regardless of which side supplied split components vs a single combined line).'
  };
}
