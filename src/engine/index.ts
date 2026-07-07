/**
 * The Rx Verify matching engine.
 *
 * verify() compares a ScriptData (incoming e-prescription) against
 * EnteredData (what the technician entered in PioneerRx) and produces a
 * FieldVerdict for every field, always in FIELD_ORDER, never re-sorted.
 */

import {
  FIELD_ORDER,
  type Address,
  type DrugDescriptor,
  type FieldVerdict,
  type ScriptData,
  type EnteredData,
  type VerifyResult
} from '../types.js';
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
import { compareDaw } from '../daw/index.js';

/**
 * Display formatters are DISCRIMINATED BY CALL SITE, not by duck-typing
 * the runtime shape of an unknown value. Every place below that needs to
 * turn a source/entered value into display text knows statically which
 * field it's formatting (Address, DrugDescriptor, or a plain scalar) —
 * so it calls the matching formatter directly instead of asking
 * "does this object happen to have exactly these keys?".
 *
 * This replaces an earlier version that used exact-key-set duck typing
 * (`Object.keys(v).every(k => [...].includes(k))`) to decide whether an
 * object was "address-like" or "drug-like" before falling back to
 * `JSON.stringify(v)` for anything that didn't match. That fallback is
 * exactly the raw-object-in-the-UI failure mode Will hit: any object
 * carrying so much as one extra/differently-cased key (a real risk
 * across a JSON hop from a hand-maintained C# mirror of these types —
 * see overlay/RxVerifyOverlay/Models/EngineModels.cs) would silently
 * fail the shape check and render as literal JSON in the overlay
 * instead of a human-readable line. Calling the right formatter by
 * construction removes that whole class of bug — there's no shape
 * check left to fail.
 */

function stringifyScalar(v: string | number | null | undefined): string | null {
  if (v === null || v === undefined || v === '') return null;
  return String(v);
}

/**
 * Addresses (patientAddress, prescriberAddress) display as one
 * human-readable line — "street, city, state zip" — instead of raw
 * JSON, so the source/entered columns are directly comparable at a
 * glance regardless of which side supplied split components vs a
 * single combined string (see normalize/address.ts).
 */
function stringifyAddress(v: Address | null | undefined): string | null {
  if (!v) return null;
  const street = [v.street, v.unit].filter((p) => typeof p === 'string' && p).join(' Unit ');
  const cityStateZip = [v.city, [v.state, v.zip].filter(Boolean).join(' ')]
    .filter((p) => typeof p === 'string' && p)
    .join(', ');
  return [street, cityStateZip].filter(Boolean).join(', ') || null;
}

/**
 * Drug is displayed by NAME only — never the NDC (see src/drug/index.ts:
 * real dispensed NDCs routinely differ from the e-script's stated NDC,
 * so showing it side-by-side reads as a false mismatch to a glance).
 */
function stringifyDrug(v: DrugDescriptor | null | undefined): string | null {
  if (!v) return null;
  return typeof v.name === 'string' && v.name ? v.name : null;
}

/** Display text for the SOURCE side of the daw field (source.substitutionsNotAllowed). */
function stringifySubstitutionIndicator(v: boolean | null | undefined): string | null {
  if (v === null || v === undefined) return null;
  return v ? 'Substitution NOT allowed (DAW)' : 'Substitution allowed';
}

/** Display text for the ENTERED side of the daw field (entered.daw). */
function stringifyDaw(v: boolean | null | undefined): string | null {
  if (v === null || v === undefined) return null;
  return v ? 'DAW checked' : 'DAW not checked';
}

export interface VerifyOptions {
  /**
   * Skip the drug-identity lookup (compareDrugs, which consults
   * RxNormProvider — the LocalNdcProvider backing it loads/decompresses
   * a ~130k-concept dataset, see src/drug/index.ts). Every OTHER field
   * (name/DOB/address/prescriber/sig/quantity/refills) is pure string/
   * date/number comparison and is effectively instant — there is no
   * reason those should wait on the drug lookup.
   *
   * Added per Will's live-test feedback: clicking Refresh in the
   * overlay had a noticeable lag before ANY field updated, because the
   * old single verify() call blocked on the drug lookup before
   * returning anything at all. The overlay now calls verify() twice per
   * refresh (see overlay/RxVerifyOverlay/ViewModels/OverlayViewModel.cs
   * RefreshAsync + Engine/EngineClient.cs): once with
   * skipDrugLookup=true for an immediate render of every field except
   * drug, and once (in the background, not blocking the UI) with
   * skipDrugLookup=false (or omitted) for the real drug verdict, which
   * then updates just that one row when it resolves.
   *
   * When true, the drug field's sourceValue/enteredValue are still
   * populated (stringifyDrug is cheap — no provider lookup), only the
   * comparison verdict itself is deferred — so the overlay can show the
   * actual drug names immediately with a "computing" indicator instead
   * of a blank field.
   */
  skipDrugLookup?: boolean;
}

/** Reason code the drug field carries while skipDrugLookup defers the real comparison — see VerifyOptions.skipDrugLookup. Callers (the overlay) check for this exact code to know a field is still computing, not actually unverifiable. */
export const PENDING_DRUG_LOOKUP_REASON_CODE = 'pending_lookup';

export function verify(
  source: ScriptData,
  entered: EnteredData,
  provider: RxNormProvider,
  options: VerifyOptions = {}
): VerifyResult {
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
  const drugResult = options.skipDrugLookup
    ? {
        status: 'yellow' as const,
        reasonCode: PENDING_DRUG_LOOKUP_REASON_CODE,
        explanation: 'Drug identity lookup is still running against the local NDC dataset — this field will update in place.'
      }
    : compareDrugs(source.drug, entered.drug, provider);
  const sigResult = compareSigs(source.sig, entered.sig);
  const quantityResult = compareQuantity(
    source.quantity,
    source.quantityUnit,
    entered.quantity,
    entered.quantityUnit,
    enteredSigParsed
  );
  const refillsResult = compareRefills(source.refills, entered.refills);
  const dawResult = compareDaw(source.substitutionsNotAllowed, entered.daw);

  const verdicts: FieldVerdict[] = [
    {
      field: 'patientName',
      ...nameResult,
      sourceValue: stringifyScalar(source.patientName),
      enteredValue: stringifyScalar(entered.patientName)
    },
    {
      field: 'patientDOB',
      ...dobResult,
      sourceValue: stringifyScalar(source.patientDOB),
      enteredValue: stringifyScalar(entered.patientDOB)
    },
    {
      field: 'patientAddress',
      ...addressResult,
      sourceValue: stringifyAddress(source.patientAddress),
      enteredValue: stringifyAddress(entered.patientAddress)
    },
    {
      field: 'prescriberName',
      ...prescriberNameResult,
      sourceValue: stringifyScalar(source.prescriber?.name),
      enteredValue: stringifyScalar(entered.prescriber?.name)
    },
    {
      field: 'prescriberNpi',
      ...prescriberNpiResult,
      sourceValue: stringifyScalar(source.prescriber?.npi),
      enteredValue: stringifyScalar(entered.prescriber?.npi)
    },
    {
      field: 'prescriberPhone',
      ...prescriberPhoneResult,
      sourceValue: stringifyScalar(source.prescriber?.phone),
      enteredValue: stringifyScalar(entered.prescriber?.phone)
    },
    {
      field: 'prescriberAddress',
      ...prescriberAddressResult,
      sourceValue: stringifyAddress(source.prescriber?.address),
      enteredValue: stringifyAddress(entered.prescriber?.address)
    },
    {
      field: 'dateWritten',
      ...dateWrittenResult,
      sourceValue: stringifyScalar(source.dateWritten),
      enteredValue: stringifyScalar(entered.dateWritten)
    },
    {
      field: 'drug',
      ...drugResult,
      sourceValue: stringifyDrug(source.drug),
      enteredValue: stringifyDrug(entered.drug)
    },
    {
      field: 'sig',
      ...sigResult,
      sourceValue: stringifyScalar(source.sig),
      enteredValue: stringifyScalar(entered.sig)
    },
    {
      field: 'quantity',
      ...quantityResult,
      sourceValue: stringifyScalar(source.quantity),
      enteredValue: stringifyScalar(entered.quantity)
    },
    {
      field: 'refills',
      ...refillsResult,
      sourceValue: stringifyScalar(source.refills),
      enteredValue: stringifyScalar(entered.refills)
    },
    {
      field: 'daw',
      ...dawResult,
      sourceValue: stringifySubstitutionIndicator(source.substitutionsNotAllowed),
      enteredValue: stringifyDaw(entered.daw)
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
