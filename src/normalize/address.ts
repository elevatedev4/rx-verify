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

/** USPS Pub 28 common street-suffix abbreviations (subset). */
const STREET_SUFFIXES: Record<string, string> = {
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

interface NormalizedStreet {
  /** Street line with unit stripped out, tokens normalized. */
  base: string;
  unit: string | null;
}

/** Split a raw street line into base + unit, and normalize tokens. */
function normalizeStreetLine(raw: string): NormalizedStreet {
  let s = foldCase(raw);

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

  return { base: normalized.join(' '), unit: unit ? unit.toLowerCase() : null };
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
  const m = /^(.*?),\s*([A-Za-z]{2})\s*(\d{5}(?:-\d{4})?)?\s*$/.exec(trimmed);
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
  // NOT implemented: fuzzy/typo tolerance on the street NAME text itself
  // (e.g. "Wellz" vs "Wells"). That would need real edit-distance
  // matching, and — per this engine's stated philosophy elsewhere (see
  // src/drug/index.ts's "can only ever fail toward MORE yellow, never a
  // false green") — a permissive fuzzy-string match on an address risks
  // treating two DIFFERENT streets as the same one, i.e. a false GREEN,
  // which this engine avoids everywhere else. Abbreviation normalization
  // (already handled) covers the realistic "different formatting, same
  // address" case without that risk.
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

  const streetDiffers = componentDiffers(srcStreet.base, entStreet.base);
  const cityDiffers = componentDiffers(srcCity, entCity);
  const stateDiffers = componentDiffers(srcState, entState);
  const zipDiffers = componentDiffers(srcZip, entZip);

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
