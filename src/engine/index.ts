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
import {
  compareQuantity,
  compareRefills,
  comparePrescriberName,
  comparePrescriberNpi,
  comparePrescriberPhone,
  comparePrescriberAddress
} from '../quantity/index.js';

/** True for anything shaped like the Address interface (street/unit/city/state/zip keys only). */
function isAddressLike(v: Record<string, unknown>): boolean {
  const keys = Object.keys(v);
  return keys.length > 0 && keys.every((k) => ['street', 'unit', 'city', 'state', 'zip'].includes(k));
}

function stringifyValue(v: unknown): string | null {
  if (v === null || v === undefined || v === '') return null;
  if (typeof v === 'object') {
    const record = v as Record<string, unknown>;
    const parts = Object.values(record).filter((p) => p !== undefined && p !== null && p !== '');
    if (parts.length === 0) return null;
    // Addresses (patientAddress, prescriberAddress) display as one
    // human-readable line — "street, city, state zip" — instead of raw
    // JSON, so the source/entered columns are directly comparable at a
    // glance regardless of which side supplied split components vs a
    // single combined string (see normalize/address.ts).
    if (isAddressLike(record)) {
      const street = [record.street, record.unit].filter((p) => typeof p === 'string' && p).join(' Unit ');
      const cityStateZip = [record.city, [record.state, record.zip].filter(Boolean).join(' ')]
        .filter((p) => typeof p === 'string' && p)
        .join(', ');
      return [street, cityStateZip].filter(Boolean).join(', ') || null;
    }
    // Drug is displayed by NAME only — never the NDC (see src/drug/index.ts:
    // real dispensed NDCs routinely differ from the e-script's stated NDC,
    // so showing it side-by-side reads as a false mismatch to a glance).
    if (Object.keys(record).every((k) => ['name', 'ndc'].includes(k))) {
      return typeof record.name === 'string' && record.name ? record.name : null;
    }
    return JSON.stringify(v);
  }
  return String(v);
}

export function verify(source: ScriptData, entered: EnteredData, provider: RxNormProvider): VerifyResult {
  const enteredSigParsed = entered.sig ? parseSig(entered.sig) : null;

  const nameResult = compareNames(source.patientName, entered.patientName);
  // DOB is pastOnly: a 2-digit year that would window into the future
  // (e.g. "3/5/45" -> 2045) re-windows to the 1900s instead.
  const dobResult = compareDates(source.patientDOB, entered.patientDOB, { pastOnly: true });
  const addressResult = compareAddresses(source.patientAddress, entered.patientAddress);
  const prescriberNameResult = comparePrescriberName(source.prescriber?.name, entered.prescriber?.name);
  const prescriberNpiResult = comparePrescriberNpi(source.prescriber?.npi, entered.prescriber?.npi);
  const prescriberPhoneResult = comparePrescriberPhone(source.prescriber?.phone, entered.prescriber?.phone);
  const prescriberAddressResult = comparePrescriberAddress(source.prescriber?.address, entered.prescriber?.address);
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
      field: 'prescriberName',
      ...prescriberNameResult,
      sourceValue: stringifyValue(source.prescriber?.name),
      enteredValue: stringifyValue(entered.prescriber?.name)
    },
    {
      field: 'prescriberNpi',
      ...prescriberNpiResult,
      sourceValue: stringifyValue(source.prescriber?.npi),
      enteredValue: stringifyValue(entered.prescriber?.npi)
    },
    {
      field: 'prescriberPhone',
      ...prescriberPhoneResult,
      sourceValue: stringifyValue(source.prescriber?.phone),
      enteredValue: stringifyValue(entered.prescriber?.phone)
    },
    {
      field: 'prescriberAddress',
      ...prescriberAddressResult,
      sourceValue: stringifyValue(source.prescriber?.address),
      enteredValue: stringifyValue(entered.prescriber?.address)
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
