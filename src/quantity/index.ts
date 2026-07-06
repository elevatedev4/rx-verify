/**
 * Quantity, refills, and prescriber comparison.
 *
 * Quantity verdict philosophy:
 *  - Unit-normalized exact match = GREEN.
 *  - If quantities differ but sig math reconciles at SOME whole-number
 *    days supply (e.g. a 90-day script filled as a 30-day insurance
 *    split) = YELLOW quantity_adjusted.
 *  - If quantities differ and do NOT reconcile with sig math = RED.
 *
 * Days supply is intentionally NOT compared anywhere in this module (or
 * anywhere in the engine) — removed per pharmacist feedback: it isn't a
 * meaningful discrepancy signal for this workflow, so it's neither
 * checked nor surfaced in any category.
 *
 * Refills: exact = GREEN, else RED (no legitimate-difference category).
 *
 * Prescriber is FOUR separate comparisons (name, NPI, phone, address),
 * each producing its own FieldVerdict — see comparePrescriberName/
 * Npi/Phone/Address below. A bundled single "prescriber" verdict used to
 * hide which specific piece actually differed; splitting it out lets the
 * pharmacist see e.g. "NPI matches, phone differs" instead of one vague
 * mismatch. NPI is the authoritative identifier (RED on mismatch); name
 * uses the same rules as patient name (module 1); phone and address are
 * never RED on their own — a differing callback number or office address
 * doesn't mean a different prescriber, just needs a glance.
 */

import { compareNames } from '../normalize/name.js';
import { compareAddresses } from '../normalize/address.js';
import type { ParsedSig } from '../sig/index.js';
import type { Address } from '../types.js';

export type SimpleStatus = 'green' | 'yellow' | 'red';

export interface CompareResult {
  status: SimpleStatus;
  reasonCode: string;
  explanation: string;
}

const UNIT_ALIASES: Record<string, string> = {
  tablet: 'tab', tablets: 'tab', tab: 'tab', tabs: 'tab',
  capsule: 'cap', capsules: 'cap', cap: 'cap', caps: 'cap',
  ml: 'ml', milliliter: 'ml', milliliters: 'ml',
  g: 'g', gram: 'g', grams: 'g',
  gtt: 'gtt', gtts: 'gtt', drop: 'gtt', drops: 'gtt'
};

function normalizeUnit(raw: string | null | undefined): string | null {
  if (!raw) return null;
  const folded = raw.toLowerCase().trim();
  return UNIT_ALIASES[folded] ?? folded;
}

function toNumber(v: string | number | null | undefined): number | null {
  if (v === null || v === undefined || v === '') return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}

/**
 * Determine whether `sourceQty` and `enteredQty` can be reconciled by an
 * alternate whole-number days-supply split, using the entered sig's
 * dose-per-administration and frequency. E.g. sig = 1 tab BID (2/day),
 * source qty 90 (45-day supply) vs entered qty 30 (15-day supply, i.e.
 * a common insurance 90->30 split) both reconcile against the same sig.
 *
 * Plausibility bounds (so absurd pairs like 1 vs 1,000,000 never get a
 * legitimate-difference pass):
 *  - each implied days supply must be a whole number in [1, 120]
 *  - the two implied supplies must form a sane split: an integer ratio
 *    no greater than 6 (covers 90->30, 60->30, 100->50, 90->15, ...)
 * Anything outside these bounds is NOT a recognized split and falls
 * through to RED quantity_mismatch.
 */
const MAX_PLAUSIBLE_DAYS_SUPPLY = 120;
const MAX_SPLIT_RATIO = 6;

function reconcilesViaSigSplit(
  sourceQty: number,
  enteredQty: number,
  sig: ParsedSig | null
): { reconciles: boolean; sourceDays: number | null; enteredDays: number | null } {
  const no = { reconciles: false, sourceDays: null, enteredDays: null };
  if (!sig || sig.doseCount === null || sig.timesPerDay === null || sig.doseCount <= 0 || sig.timesPerDay <= 0) {
    return no;
  }
  const perDay = sig.doseCount * sig.timesPerDay;
  const sourceDaysRaw = sourceQty / perDay;
  const enteredDaysRaw = enteredQty / perDay;

  const isWholeish = (n: number) => Math.abs(n - Math.round(n)) < 0.05;

  if (!isWholeish(sourceDaysRaw) || !isWholeish(enteredDaysRaw)) return no;

  const sourceDays = Math.round(sourceDaysRaw);
  const enteredDays = Math.round(enteredDaysRaw);

  if (sourceDays < 1 || sourceDays > MAX_PLAUSIBLE_DAYS_SUPPLY) return no;
  if (enteredDays < 1 || enteredDays > MAX_PLAUSIBLE_DAYS_SUPPLY) return no;

  const ratio = Math.max(sourceDays, enteredDays) / Math.min(sourceDays, enteredDays);
  if (!isWholeish(ratio) || ratio > MAX_SPLIT_RATIO) return no;

  return { reconciles: true, sourceDays, enteredDays };
}

export function compareQuantity(
  sourceQty: string | number | null | undefined,
  sourceUnit: string | null | undefined,
  enteredQty: string | number | null | undefined,
  enteredUnit: string | null | undefined,
  enteredSig: ParsedSig | null = null
): CompareResult {
  const srcEmpty = sourceQty === null || sourceQty === undefined || sourceQty === '';
  const entEmpty = enteredQty === null || enteredQty === undefined || enteredQty === '';

  if (srcEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'Source e-prescription did not provide a quantity to compare.' };
  }
  if (entEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'No quantity was entered in PioneerRx to compare against the source.' };
  }

  const a = toNumber(sourceQty);
  const b = toNumber(enteredQty);
  const uA = normalizeUnit(sourceUnit);
  const uB = normalizeUnit(enteredUnit);

  if (a === null || b === null) {
    return { status: 'yellow', reasonCode: 'unparseable_quantity', explanation: `Could not parse quantity ("${sourceQty}" / "${enteredQty}") as a number.` };
  }

  const unitsCompatible = !uA || !uB || uA === uB;

  if (a === b && unitsCompatible) {
    return { status: 'green', reasonCode: 'exact_match', explanation: 'Quantity matches exactly after unit normalization.' };
  }

  if (!unitsCompatible) {
    return {
      status: 'red',
      reasonCode: 'unit_mismatch',
      explanation: `Quantity units differ in a way that cannot be reconciled: "${uA}" vs "${uB}".`
    };
  }

  const split = reconcilesViaSigSplit(a, b, enteredSig);
  if (split.reconciles) {
    return {
      status: 'yellow',
      reasonCode: 'quantity_adjusted',
      explanation: `Quantity differs (${a} vs ${b}) but reconciles with sig math as a ${split.sourceDays}-day vs ${split.enteredDays}-day supply split (e.g. insurance fill limit).`
    };
  }

  return {
    status: 'red',
    reasonCode: 'quantity_mismatch',
    explanation: `Quantity differs (${a} vs ${b}) and does not reconcile with sig-based dosing math.`
  };
}

export function compareRefills(
  sourceRaw: string | number | null | undefined,
  enteredRaw: string | number | null | undefined
): CompareResult {
  const srcEmpty = sourceRaw === null || sourceRaw === undefined || sourceRaw === '';
  const entEmpty = enteredRaw === null || enteredRaw === undefined || enteredRaw === '';

  if (srcEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'Source e-prescription did not provide a refill count to compare.' };
  }
  if (entEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'No refill count was entered in PioneerRx to compare against the source.' };
  }

  const a = toNumber(sourceRaw);
  const b = toNumber(enteredRaw);
  if (a === null || b === null) {
    return { status: 'yellow', reasonCode: 'unparseable_quantity', explanation: `Could not parse refill count ("${sourceRaw}" / "${enteredRaw}") as a number.` };
  }
  if (a === b) {
    return { status: 'green', reasonCode: 'exact_match', explanation: 'Refill count matches exactly.' };
  }
  return { status: 'red', reasonCode: 'refills_mismatch', explanation: `Refill count differs: ${a} vs ${b}.` };
}

/**
 * Prescriber NAME field — same rules as patient name (module 1),
 * independent of NPI. Previously, a matching NPI would force this to
 * GREEN even when the name text differed ("name_variant"); now that name
 * and NPI are separate fields, each is judged on its own terms — a
 * spelling variant is still a legitimate (yellow) name-level difference
 * worth a glance, even though the NPI field alongside it will be green.
 */
export function comparePrescriberName(
  sourceName: string | null | undefined,
  enteredName: string | null | undefined
): CompareResult {
  return compareNames(sourceName, enteredName);
}

/** Prescriber NPI field — an NPI is an unambiguous identifier: exact match is GREEN, any digit difference is RED (never a legitimate difference). */
export function comparePrescriberNpi(
  sourceNpi: string | null | undefined,
  enteredNpi: string | null | undefined
): CompareResult {
  const srcEmpty = !sourceNpi || !sourceNpi.trim();
  const entEmpty = !enteredNpi || !enteredNpi.trim();

  if (srcEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'Source e-prescription did not provide a prescriber NPI to compare.' };
  }
  if (entEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'No prescriber NPI was entered in PioneerRx to compare against the source.' };
  }

  const npiA = sourceNpi.replace(/\D/g, '');
  const npiB = enteredNpi.replace(/\D/g, '');

  if (npiA === npiB) {
    return { status: 'green', reasonCode: 'exact_match', explanation: `Prescriber NPI matches exactly (${npiA}).` };
  }
  return { status: 'red', reasonCode: 'npi_mismatch', explanation: `Prescriber NPI differs: ${npiA} vs ${npiB}.` };
}

/**
 * Prescriber PHONE field. Never RED on its own — a clinic often has more
 * than one legitimate number (direct line vs front desk vs after-hours),
 * so a differing phone doesn't imply a different prescriber the way a
 * differing NPI does. Compared on digits only (formatting-agnostic).
 */
export function comparePrescriberPhone(
  sourcePhone: string | null | undefined,
  enteredPhone: string | null | undefined
): CompareResult {
  const srcEmpty = !sourcePhone || !sourcePhone.trim();
  const entEmpty = !enteredPhone || !enteredPhone.trim();

  if (srcEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'Source e-prescription did not provide a prescriber phone number to compare.' };
  }
  if (entEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'No prescriber phone number was entered in PioneerRx to compare against the source.' };
  }

  const digitsA = sourcePhone.replace(/\D/g, '');
  const digitsB = enteredPhone.replace(/\D/g, '');
  // Compare on the last 10 digits so a leading US country code ("1") on
  // only one side doesn't cause a false difference.
  const tailA = digitsA.slice(-10);
  const tailB = digitsB.slice(-10);

  if (tailA && tailA === tailB) {
    return { status: 'green', reasonCode: 'exact_match', explanation: 'Prescriber phone number matches exactly.' };
  }
  return {
    status: 'yellow',
    reasonCode: 'phone_differs',
    explanation: `Prescriber phone number differs ("${sourcePhone}" vs "${enteredPhone}") — a clinic can have more than one legitimate number; verify if needed.`
  };
}

/**
 * Prescriber ADDRESS field — same "never RED" philosophy as patient
 * address (practices move/have multiple locations); reuses
 * compareAddresses so both fields normalize identically (street-suffix/
 * directional abbreviations, freeform-vs-component tolerance for the
 * entered side's single combined string vs the source's split fields).
 */
export function comparePrescriberAddress(
  sourceAddress: Address | null | undefined,
  enteredAddress: Address | null | undefined
): CompareResult {
  return compareAddresses(sourceAddress, enteredAddress);
}
