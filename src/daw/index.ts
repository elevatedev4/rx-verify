/**
 * DAW / substitution comparison.
 *
 * The e-script's MedicationPrescribed > Substitutions indicator states
 * whether the prescriber allows the pharmacy to substitute (NCPDP SCRIPT
 * codes; code 1 = "Substitution Not Allowed by Prescriber", every other
 * observed code permits substitution). PioneerRx's entered side carries a
 * DAW ("Dispense As Written") checkbox (AutomationId uxDawCode — see
 * overlay Uia/FieldMap.cs) — this engine only ever sees that boolean, not
 * a numeric DAW code (0/1/2/...), so there is no way to distinguish
 * "pharmacist checked DAW because the patient requested brand" from any
 * other checked case; see compareDaw's own doc below for how a future
 * numeric-code input would need to change this.
 *
 * The two sides are an EQUIVALENCE, not a one-way requirement (owner
 * correction, in-app report on build 5bd8d96, verbatim: "DAW checked
 * means substitutions not allowed. DAW unchecked matches substitutions
 * allowed."):
 *   - substitution allowed + DAW unchecked   -> green (consistent)
 *   - substitution allowed + DAW checked     -> red (mismatch — pharmacy
 *     entered "dispense as written" but the prescriber allows substitution)
 *   - substitution NOT allowed + DAW checked -> green (consistent)
 *   - substitution NOT allowed + DAW unchecked -> red (daw_required)
 *
 * Missing data on EITHER side is yellow not_provided, same philosophy as
 * every other comparison in this engine — never a hard mismatch just
 * because a field wasn't read/available.
 */

export type SimpleStatus = 'green' | 'yellow' | 'red';

export interface CompareResult {
  status: SimpleStatus;
  reasonCode: string;
  explanation: string;
}

export function compareDaw(
  sourceSubstitutionsNotAllowed: boolean | null | undefined,
  enteredDaw: boolean | null | undefined
): CompareResult {
  if (sourceSubstitutionsNotAllowed === null || sourceSubstitutionsNotAllowed === undefined) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription did not provide a substitution indicator to compare.'
    };
  }

  if (!sourceSubstitutionsNotAllowed) {
    if (enteredDaw) {
      return {
        status: 'red',
        reasonCode: 'daw_checked_but_substitution_allowed',
        explanation: 'E-prescription allows substitution, but DAW is checked — uncheck DAW (or confirm the prescriber/patient requires brand).'
      };
    }

    return {
      status: 'green',
      reasonCode: 'substitution_allowed',
      explanation: 'Source e-prescription allows substitution and the DAW checkbox is not checked — consistent.'
    };
  }

  // Substitution is NOT allowed by the prescriber — DAW must be checked.
  if (enteredDaw === null || enteredDaw === undefined) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription indicates substitution is NOT allowed by the prescriber, but the DAW checkbox state was not read from PioneerRx to compare — verify DAW is checked.'
    };
  }

  if (enteredDaw) {
    return {
      status: 'green',
      reasonCode: 'daw_consistent',
      explanation: 'Source e-prescription disallows substitution and the DAW checkbox is checked — consistent.'
    };
  }

  return {
    status: 'red',
    reasonCode: 'daw_required',
    explanation: 'Source e-prescription indicates substitution is NOT allowed by the prescriber (DAW), but the entered DAW checkbox is NOT checked.'
  };
}
