/**
 * Shared types for the Rx Verify matching engine.
 *
 * SYNTHETIC DATA ONLY. Nothing in this repo, including tests and fixtures,
 * may ever contain real patient, prescriber, or prescription data.
 *
 * These types are intentionally plain data (JSON-serializable) so the
 * engine can be ported to C#/.NET or run behind a sidecar process without
 * any change to the core logic.
 */

export type Verdict_Status = 'green' | 'yellow' | 'red';

/** One field's comparison result, in the fixed review order. */
export interface FieldVerdict {
  field: FieldName;
  status: Verdict_Status;
  reasonCode: string;
  explanation: string;
  sourceValue: string | null;
  enteredValue: string | null;
}

/**
 * FIXED FIELD ORDER — hard requirement from the owner (a pharmacist).
 * The engine's output array is always in this order, never sorted by
 * severity or anything else.
 *
 * Prescriber is FOUR separate fields (name/NPI/phone/address), each with
 * its own verdict, per the pharmacist's live-test feedback — a bundled
 * "prescriber" field hid which specific piece (name vs NPI vs phone vs
 * address) actually differed. daysSupply has been REMOVED entirely (not
 * compared, not displayed) per the same feedback round.
 */
export const FIELD_ORDER = [
  'patientName',
  'patientDOB',
  'patientAddress',
  'prescriberName',
  'prescriberNpi',
  'prescriberPhone',
  'prescriberAddress',
  'dateWritten',
  'quantity',
  'refills',
  'daw',
  'drug',
  'sig'
] as const;

export type FieldName = (typeof FIELD_ORDER)[number];

export interface Address {
  street?: string;
  unit?: string;
  city?: string;
  state?: string;
  zip?: string;
}

export interface Prescriber {
  name?: string;
  npi?: string;
  /** Prescriber's office phone, any common format (digits, dashes, parens). */
  phone?: string;
  /** Prescriber's office address. Entered side is typically one combined string (Street only); source is split into components — see normalize/address.ts. */
  address?: Address;
}

export interface DrugDescriptor {
  /** Raw display name as it appears on the record, e.g. "Zestril 10mg tablet". */
  name?: string;
  /** NDC code if known, in any of the common 10/11-digit formats. */
  ndc?: string;
}

/** One side (source e-prescription, or technician-entered data) of a comparison. */
export interface PrescriptionRecord {
  patientName?: string;
  patientDOB?: string;
  patientAddress?: Address;
  prescriber?: Prescriber;
  dateWritten?: string;
  drug?: DrugDescriptor;
  sig?: string;
  quantity?: string | number;
  quantityUnit?: string;
  refills?: string | number;
  /**
   * SOURCE-side only: true when the e-script's MedicationPrescribed >
   * Substitutions indicator states the prescriber does NOT allow
   * substitution (NCPDP SCRIPT code 1, "Substitution Not Allowed by
   * Prescriber") — i.e. DAW is effectively required. false for any other
   * observed code (0 "No Product Selection Indicated", or a
   * patient-requested-brand code, all of which permit the pharmacy to
   * dispense generic); undefined when the e-script didn't provide a
   * Substitutions indicator at all. Never meaningfully set on the
   * entered side (see `daw` below for that side of the comparison).
   */
  substitutionsNotAllowed?: boolean;
  /**
   * ENTERED-side only: whether PioneerRx's DAW checkbox (AutomationId
   * uxDawCode) is checked. Never meaningfully set on the source side
   * (see `substitutionsNotAllowed` above for that side).
   */
  daw?: boolean;
}

/** The incoming e-prescription — the presumed source of truth. */
export type ScriptData = PrescriptionRecord;

/** What the pharmacy technician entered into PioneerRx. */
export type EnteredData = PrescriptionRecord;

export interface VerifySummary {
  green: number;
  yellow: number;
  red: number;
  total: number;
}

export interface VerifyResult {
  verdicts: FieldVerdict[];
  summary: VerifySummary;
}
