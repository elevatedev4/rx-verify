/**
 * Quantity, days supply, refills, and prescriber comparison.
 *
 * Quantity verdict philosophy:
 *  - Unit-normalized exact match = GREEN.
 *  - If quantities differ but sig math reconciles at SOME whole-number
 *    days supply (e.g. a 90-day script filled as a 30-day insurance
 *    split) = YELLOW quantity_adjusted.
 *  - If quantities differ and do NOT reconcile with sig math = RED.
 *
 * Days supply: absent on the source is normal (NCPDP optional field) =
 * YELLOW not_provided, never a mismatch.
 *
 * Refills: exact = GREEN, else RED (no legitimate-difference category).
 *
 * Prescriber: compare by NPI when both sides provide one — exact NPI
 * match is GREEN even if the name spelling differs (note name_variant).
 * NPI mismatch is RED (an NPI is an unambiguous identifier). When NPI is
 * absent on either side, fall back to name comparison (module 1 rules).
 */

import { compareNames } from '../normalize/name.js';
import type { ParsedSig } from '../sig/index.js';
import type { Prescriber } from '../types.js';

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
 */
function reconcilesViaSigSplit(
  sourceQty: number,
  enteredQty: number,
  sig: ParsedSig | null
): { reconciles: boolean; sourceDays: number | null; enteredDays: number | null } {
  if (!sig || sig.doseCount === null || sig.timesPerDay === null || sig.doseCount <= 0 || sig.timesPerDay <= 0) {
    return { reconciles: false, sourceDays: null, enteredDays: null };
  }
  const perDay = sig.doseCount * sig.timesPerDay;
  const sourceDaysRaw = sourceQty / perDay;
  const enteredDaysRaw = enteredQty / perDay;

  const isWholeish = (n: number) => Math.abs(n - Math.round(n)) < 0.05;

  if (isWholeish(sourceDaysRaw) && isWholeish(enteredDaysRaw)) {
    return { reconciles: true, sourceDays: Math.round(sourceDaysRaw), enteredDays: Math.round(enteredDaysRaw) };
  }
  return { reconciles: false, sourceDays: null, enteredDays: null };
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

export function compareDaysSupply(
  sourceRaw: string | number | null | undefined,
  enteredRaw: string | number | null | undefined
): CompareResult {
  const srcEmpty = sourceRaw === null || sourceRaw === undefined || sourceRaw === '';
  const entEmpty = enteredRaw === null || enteredRaw === undefined || enteredRaw === '';

  if (srcEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription did not provide days supply — this is an optional NCPDP field, not a discrepancy.'
    };
  }
  if (entEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'No days supply was entered in PioneerRx to compare against the source.' };
  }

  const a = toNumber(sourceRaw);
  const b = toNumber(enteredRaw);
  if (a === null || b === null) {
    return { status: 'yellow', reasonCode: 'unparseable_quantity', explanation: `Could not parse days supply ("${sourceRaw}" / "${enteredRaw}") as a number.` };
  }
  if (a === b) {
    return { status: 'green', reasonCode: 'exact_match', explanation: 'Days supply matches exactly.' };
  }
  return { status: 'red', reasonCode: 'days_supply_mismatch', explanation: `Days supply differs: ${a} vs ${b}.` };
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

export function comparePrescriber(
  sourceRaw: Prescriber | null | undefined,
  enteredRaw: Prescriber | null | undefined
): CompareResult {
  const sourceEmpty = !sourceRaw || (!sourceRaw.name && !sourceRaw.npi);
  const enteredEmpty = !enteredRaw || (!enteredRaw.name && !enteredRaw.npi);

  if (sourceEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'Source e-prescription did not provide a prescriber to compare.' };
  }
  if (enteredEmpty) {
    return { status: 'yellow', reasonCode: 'not_provided', explanation: 'No prescriber was entered in PioneerRx to compare against the source.' };
  }

  const src = sourceRaw as Prescriber;
  const ent = enteredRaw as Prescriber;

  if (src.npi && ent.npi) {
    const npiA = src.npi.replace(/\D/g, '');
    const npiB = ent.npi.replace(/\D/g, '');
    if (npiA === npiB) {
      const namesDiffer =
        src.name && ent.name && src.name.trim().toLowerCase() !== ent.name.trim().toLowerCase();
      if (namesDiffer) {
        return {
          status: 'green',
          reasonCode: 'name_variant',
          explanation: `NPI matches exactly (${npiA}); prescriber name is spelled differently ("${src.name}" vs "${ent.name}") but the identifier confirms it's the same prescriber.`
        };
      }
      return { status: 'green', reasonCode: 'exact_match', explanation: `NPI matches exactly (${npiA}).` };
    }
    return {
      status: 'red',
      reasonCode: 'npi_mismatch',
      explanation: `Prescriber NPI differs: ${npiA} vs ${npiB}.`
    };
  }

  // Fall back to name comparison per module 1 rules.
  const nameResult = compareNames(src.name, ent.name);
  return nameResult;
}
