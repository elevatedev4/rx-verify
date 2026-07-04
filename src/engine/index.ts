/**
 * The Rx Verify matching engine.
 *
 * verify() compares a ScriptData (incoming e-prescription) against
 * EnteredData (what the technician entered in PioneerRx) and produces a
 * FieldVerdict for every field, always in FIELD_ORDER, never re-sorted.
 */

import { FIELD_ORDER, type FieldVerdict, type ScriptData, type EnteredData, type VerifyResult } from '../types.js';
import { compareNames } from '../normalize/name.js';
import { compareDates } from '../normalize/date.js';
import { compareAddresses } from '../normalize/address.js';
import { compareSigs, parseSig } from '../sig/index.js';
import { compareDrugs, type RxNormProvider } from '../drug/index.js';
import { compareQuantity, compareDaysSupply, compareRefills, comparePrescriber } from '../quantity/index.js';

function stringifyValue(v: unknown): string | null {
  if (v === null || v === undefined || v === '') return null;
  if (typeof v === 'object') {
    const parts = Object.values(v as Record<string, unknown>).filter((p) => p !== undefined && p !== null && p !== '');
    if (parts.length === 0) return null;
    return JSON.stringify(v);
  }
  return String(v);
}

export function verify(source: ScriptData, entered: EnteredData, provider: RxNormProvider): VerifyResult {
  const enteredSigParsed = entered.sig ? parseSig(entered.sig) : null;

  const nameResult = compareNames(source.patientName, entered.patientName);
  const dobResult = compareDates(source.patientDOB, entered.patientDOB);
  const addressResult = compareAddresses(source.patientAddress, entered.patientAddress);
  const prescriberResult = comparePrescriber(source.prescriber, entered.prescriber);
  const dateWrittenResult = compareDates(source.dateWritten, entered.dateWritten);
  const drugResult = compareDrugs(source.drug, entered.drug, provider);
  const sigResult = compareSigs(source.sig, entered.sig);
  const quantityResult = compareQuantity(
    source.quantity,
    source.quantityUnit,
    entered.quantity,
    entered.quantityUnit,
    enteredSigParsed
  );
  const daysSupplyResult = compareDaysSupply(source.daysSupply, entered.daysSupply);
  const refillsResult = compareRefills(source.refills, entered.refills);

  const verdicts: FieldVerdict[] = [
    {
      field: 'patientName',
      ...nameResult,
      sourceValue: stringifyValue(source.patientName),
      enteredValue: stringifyValue(entered.patientName)
    },
    {
      field: 'patientDOB',
      ...dobResult,
      sourceValue: stringifyValue(source.patientDOB),
      enteredValue: stringifyValue(entered.patientDOB)
    },
    {
      field: 'patientAddress',
      ...addressResult,
      sourceValue: stringifyValue(source.patientAddress),
      enteredValue: stringifyValue(entered.patientAddress)
    },
    {
      field: 'prescriber',
      ...prescriberResult,
      sourceValue: stringifyValue(source.prescriber),
      enteredValue: stringifyValue(entered.prescriber)
    },
    {
      field: 'dateWritten',
      ...dateWrittenResult,
      sourceValue: stringifyValue(source.dateWritten),
      enteredValue: stringifyValue(entered.dateWritten)
    },
    {
      field: 'drug',
      ...drugResult,
      sourceValue: stringifyValue(source.drug),
      enteredValue: stringifyValue(entered.drug)
    },
    {
      field: 'sig',
      ...sigResult,
      sourceValue: stringifyValue(source.sig),
      enteredValue: stringifyValue(entered.sig)
    },
    {
      field: 'quantity',
      ...quantityResult,
      sourceValue: stringifyValue(source.quantity),
      enteredValue: stringifyValue(entered.quantity)
    },
    {
      field: 'daysSupply',
      ...daysSupplyResult,
      sourceValue: stringifyValue(source.daysSupply),
      enteredValue: stringifyValue(entered.daysSupply)
    },
    {
      field: 'refills',
      ...refillsResult,
      sourceValue: stringifyValue(source.refills),
      enteredValue: stringifyValue(entered.refills)
    }
  ];

  // Sanity check: verdicts must be in FIELD_ORDER. This is a hard
  // product requirement, so we assert it rather than silently trusting
  // the literal array above.
  verdicts.forEach((v, i) => {
    if (v.field !== FIELD_ORDER[i]) {
      throw new Error(`Engine output order violation: expected ${FIELD_ORDER[i]} at index ${i}, got ${v.field}`);
    }
  });

  const summary = {
    green: verdicts.filter((v) => v.status === 'green').length,
    yellow: verdicts.filter((v) => v.status === 'yellow').length,
    red: verdicts.filter((v) => v.status === 'red').length,
    total: verdicts.length
  };

  return { verdicts, summary };
}
