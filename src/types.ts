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
 *
 * 'availableDate' (round 5, fix 3) is CONDITIONAL — the engine only ever
 * emits this verdict when the source e-script actually shows an
 * "Available" date (see PrescriptionRecord.availableDate below and
 * compareWrittenOrAvailableDate in normalize/date.ts); most scripts never
 * populate it, so most verify() calls simply skip this slot. Every other
 * field above is unconditional. verify()'s own order check tolerates the
 * skip (see engine/index.ts) while still enforcing that whatever IS
 * present appears in this relative order.
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
  'availableDate',
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
  /**
   * SOURCE-side only: PioneerRx's "Available" date, when the source
   * e-script shows one (seen on refill-response layouts). When present,
   * PioneerRx displays THIS date — not the Written date — in its own
   * entered fields, so a technician's entered date is expected to match
   * Available rather than Written. See compareWrittenOrAvailableDate
   * (normalize/date.ts), which folds this into the dateWritten verdict,
   * and FIELD_ORDER's doc above for the conditional 'availableDate'
   * verdict row. Never meaningfully set on the entered side.
   */
  availableDate?: string;
  drug?: DrugDescriptor;
  sig?: string;
  quantity?: string | number;
  quantityUnit?: string;
  refills?: string | number;
  /**
   * SOURCE-side only: true when `refills` above was read off a "Total
   * fills: N" label (seen on responded refill-request e-scripts) rather
   * than an ordinary "Refills"/"Refills Authorized"/"Refills Remaining"
   * label. "Total fills" counts the initial fill PLUS refills, so the
   * refill count that should be compared against what the technician
   * entered is N-1, not the raw N stored in `refills` — see
   * compareRefills (src/quantity/index.ts), which applies that -1 only
   * when this flag is set. Never meaningfully set on the entered side.
   */
  refillsFromTotalFills?: boolean;
  /**
   * SOURCE-side, DIAGNOSTIC ONLY, OCR path only: set ONLY when `refills`
   * above ends up undefined (nothing recognized as a refill count at
   * all) — the ~40 OCR word tokens nearest any "fill"/"refill"-shaped
   * text on the page, so a filed error report carries proof of what OCR
   * actually captured instead of just "(not provided)". Built by
   * buildRefillsOcrRegionWords in src/ocr/parseEscriptOcr.ts, which
   * explicitly excludes every word already claimed by a resolved
   * patient/prescriber/drug/directions/note field — PHI-conscious by
   * construction (over-exclusion is the safe failure mode here), never a
   * guarantee that zero sensitive text could theoretically appear.
   * Never set on the entered side, never set when refills DID resolve.
   */
  refillsOcrRegionWords?: string[];
  /**
   * SOURCE-side, DIAGNOSTIC ONLY, OCR path only: set ONLY when `refills`
   * above ends up undefined — WHY it ended up undefined, independent of
   * refillsOcrRegionWords above (which is WHAT OCR saw nearby). One of:
   *   - 'no-value-paired' — parsing ran normally (at least one field
   *     label was recognized somewhere on the page) but no label match
   *     ever produced a raw refills value at all (the ordinary "nothing
   *     found" case).
   *   - 'validation-failed:not-numeric' — a raw value WAS paired but
   *     parseRefills couldn't read a leading integer out of it.
   *   - 'internal-error:label-anywhere-anchor' — findTotalFillsLabelAnywhere
   *     (src/ocr/parseEscriptOcr.ts) threw before it could resolve
   *     anything; see that call site's local try/catch doc (2026-09-15
   *     hardening fix).
   * ONLY set on every refills-undefined exit — 2026-09-16 addition,
   * field report (3rd AUTO-DIAGNOSTIC): an unresolved refills field with
   * NO reason at all gave an automatic error report nothing to go on.
   *   - 'ocr-empty' — parseEscriptOcr was called with a null/empty OCR
   *     word list; nothing was ever parsed at all.
   *   - 'no-refills-text-found' — OCR produced words, but NOTHING on the
   *     page was recognized as ANY field label at all (not just refills)
   *     — a document/screen shape this parser has no anchor on.
   * Never set on the entered side, never set when refills DID resolve.
   * Threaded through to the overlay (Integrated/VerdictFieldInfo.cs)
   * alongside refillsOcrRegionWords so an automatic diagnostic report can
   * self-describe why the OCR extraction missed, not just what nearby
   * text looked like.
   */
  refillsMissReason?: string;
  /**
   * SOURCE-side, DIAGNOSTIC ONLY, OCR path only: set ONLY when `refills`
   * above ends up undefined AND the anchor line used for
   * refillsOcrRegionWords was found via an actual matched LABEL token
   * (Pass A/B's inline/block-column pairing, or the findTotalFillsLabelAnywhere
   * pattern-anchor fallback) — the "[anchor=matched-label]"/
   * "[anchor=fill-word]"/etc tags in refillsOcrRegionWords say which
   * anchor tier actually fired; this field is the matched label's own raw
   * OCR text plus the next 6 raw tokens on that same physical row (e.g.
   * `"Total Fills 2 ( including this fill )"`), so a filed report can show
   * the exact label+value text OCR captured, not just the surrounding
   * region words. undefined whenever no label token was matched at all —
   * including when refillsOcrRegionWords' anchor came from a bare tail
   * phrase (findTotalFillsPhraseValue, which recovers a value with NO
   * "Total Fills"/"Refills" label text anywhere on the page — see that
   * function's doc) or from the plain fill-word/approval fallback tiers.
   * Built in src/ocr/parseEscriptOcr.ts (buildRefillsOcrRegionWords' call
   * site). Never set on the entered side, never set when refills DID
   * resolve.
   */
  refillsLabelSeen?: string;
  /**
   * SOURCE-side, DIAGNOSTIC ONLY, OCR path only: set ONLY when `refills`
   * above ends up undefined — how many physical OCR rows
   * (linesBeforeChromeFilter, i.e. before the defensive chrome-line
   * filter, same row set refillsOcrRegionWords/refillsLabelSeen are
   * anchored against) this document reconstructed, so a filed report has
   * a sense of how much of the page OCR actually captured (a near-empty
   * page reads very differently from a full one that still missed
   * refills). Never set on the entered side, never set when refills DID
   * resolve.
   */
  ocrLineCount?: number;
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
  /**
   * Diagnostic-only passthrough of PrescriptionRecord.refillsOcrRegionWords
   * (see that field's doc) — attached by src/cli.ts's runVerify, never by
   * verify() itself, since verify() has no knowledge of OCR at all. Only
   * present when the OCR path was used AND refills came back unresolved.
   * The C# overlay (Reporting/RxReportPayload.cs) reads this straight
   * through into the HQ error-report payload, same "diagnostic-only,
   * ignored by every other consumer" treatment as sourceInputMode/
   * refillsTotalFillsLabelSeen already get there.
   */
  refillsOcrRegionWords?: string[];
  /**
   * Diagnostic-only passthrough of PrescriptionRecord.refillsMissReason
   * (see that field's doc) — attached by src/cli.ts's runVerify, same
   * "only present when the OCR path was used AND refills came back
   * unresolved" gating as refillsOcrRegionWords above.
   */
  refillsMissReason?: string;
  /**
   * Diagnostic-only passthrough of PrescriptionRecord.refillsLabelSeen
   * (see that field's doc) — attached by src/cli.ts's runVerify, same
   * "only present when the OCR path was used AND refills came back
   * unresolved (AND, specifically for this field, an actual label token
   * was matched)" gating as refillsOcrRegionWords above.
   */
  refillsLabelSeen?: string;
  /**
   * Diagnostic-only passthrough of PrescriptionRecord.ocrLineCount (see
   * that field's doc) — attached by src/cli.ts's runVerify, same "only
   * present when the OCR path was used AND refills came back unresolved"
   * gating as refillsOcrRegionWords above.
   */
  ocrLineCount?: number;
}
