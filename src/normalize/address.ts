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
 * Build ONE normalized, whitespace-joined comparable string for an
 * address, regardless of whether it arrived as separate components
 * (city/state/zip populated — the typical SOURCE shape, built from the
 * e-script's split AddressLine1/City/StateProvince/PostalCode leaves) or
 * as a single freeform line (the typical ENTERED shape — PioneerRx's
 * uxPatientAddress/uxWrittenByAddress controls are one combined string
 * with no separate city/state/zip controls at all, e.g. "42 Fictional
 * Wells Ct Sampleville, KS"). Token order is always street -> city -> state
 * -> zip so the two shapes line up even when one is missing a trailing
 * component (e.g. the entered side routinely omits the ZIP entirely).
 */
function buildComparableAddressTokens(addr: Address): string[] {
  const hasSeparateComponents = Boolean(addr.city || addr.state || addr.zip);

  if (hasSeparateComponents) {
    const street = normalizeStreetLine(addr.street ?? '').base;
    const city = normalizeCity(addr.city ?? '');
    const state = normalizeState(addr.state ?? '');
    const zip = normalizeZip(addr.zip ?? '');
    return [street, city, state, zip].filter(Boolean).join(' ').split(' ').filter(Boolean);
  }

  // Freeform single-line case: no separate components to normalize
  // individually, so run the WHOLE string through the same
  // punctuation/case/suffix/directional/zip normalization token-by-token.
  const raw = addr.street ?? '';
  const folded = raw
    .toLowerCase()
    .replace(/[.,#]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();

  return folded
    .split(' ')
    .filter(Boolean)
    .map((tok) => {
      if (/^\d{5,9}$/.test(tok)) return tok.slice(0, 5); // zip5 or zip9 -> zip5
      if (STREET_SUFFIXES[tok]) return STREET_SUFFIXES[tok] as string;
      if (DIRECTIONALS[tok]) return DIRECTIONALS[tok] as string;
      return tok;
    });
}

/**
 * True when one token array is a PREFIX of the other (same tokens, in
 * order, up to wherever the shorter one stops) — e.g. the entered side's
 * "...sampleville ks" is a prefix of the source's "...sampleville ks
 * 54321" when the entered display simply doesn't show a ZIP at all. This is
 * intentionally more forgiving than exact equality precisely because the
 * two real shapes above (freeform single line vs split components) will
 * routinely differ in how much detail is present, not just in format.
 */
function isTokenPrefix(shorter: string[], longer: string[]): boolean {
  if (shorter.length === 0 || shorter.length > longer.length) return false;
  return shorter.every((tok, i) => tok === longer[i]);
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

  // FREEFORM TOLERANCE: when at least one side has no separate city/
  // state/zip components (the entered side's real shape — PioneerRx's
  // address controls are one combined string, see FieldReader.cs
  // ParseAddress), the component-wise comparison below can't run
  // meaningfully (it would compare the source's real city against the
  // entered side's blank city and call every real match a mismatch).
  // Fall back to a single normalized-string/token comparison instead —
  // see buildComparableAddressTokens/isTokenPrefix.
  const srcHasComponents = Boolean(src.city || src.state || src.zip);
  const entHasComponents = Boolean(ent.city || ent.state || ent.zip);
  if (!srcHasComponents || !entHasComponents) {
    const srcTokens = buildComparableAddressTokens(src);
    const entTokens = buildComparableAddressTokens(ent);
    const matches =
      srcTokens.length > 0 &&
      entTokens.length > 0 &&
      (isTokenPrefix(srcTokens, entTokens) || isTokenPrefix(entTokens, srcTokens));

    if (matches) {
      return {
        status: 'green',
        reasonCode: 'exact_match',
        explanation: 'Address matches after normalization (one side provided as a single combined line, the other as separate components).'
      };
    }

    return {
      status: 'yellow',
      reasonCode: 'address_differs',
      explanation:
        'Address differs from the source after normalization. Address alone does not block dispensing (patients move) — verify identity via DOB.'
    };
  }

  const srcStreet = normalizeStreetLine(src.street ?? '');
  const entStreet = normalizeStreetLine(ent.street ?? '');
  const srcCity = normalizeCity(src.city ?? '');
  const entCity = normalizeCity(ent.city ?? '');
  const srcState = normalizeState(src.state ?? '');
  const entState = normalizeState(ent.state ?? '');
  const srcZip = normalizeZip(src.zip ?? '');
  const entZip = normalizeZip(ent.zip ?? '');
  const srcUnit = (srcStreet.unit ?? (src.unit ? src.unit.toLowerCase() : null));
  const entUnit = (entStreet.unit ?? (ent.unit ? ent.unit.toLowerCase() : null));

  const coreEqual =
    srcStreet.base === entStreet.base &&
    srcCity === entCity &&
    srcState === entState &&
    srcZip === entZip;

  if (coreEqual && srcUnit === entUnit) {
    return {
      status: 'green',
      reasonCode: 'exact_match',
      explanation: 'Address matches exactly after street/directional/unit normalization.'
    };
  }

  if (coreEqual && srcUnit !== entUnit) {
    return {
      status: 'yellow',
      reasonCode: 'unit_differs',
      explanation: `Street, city, state, and ZIP match; unit differs ("${srcUnit ?? 'none'}" vs "${entUnit ?? 'none'}").`
    };
  }

  return {
    status: 'yellow',
    reasonCode: 'address_differs',
    explanation:
      'Street, city, or ZIP differs from the source. Address alone does not block dispensing (patients move) — verify identity via DOB.'
  };
}
