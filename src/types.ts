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
 */
export const FIELD_ORDER = [
  'patientName',
  'patientDOB',
  'patientAddress',
  'prescriber',
  'dateWritten',
  'drug',
  'sig',
  'quantity',
  'daysSupply',
  'refills'
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
  daysSupply?: string | number;
  refills?: string | number;
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
