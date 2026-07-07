/**
 * DAW / substitution comparison.
 *
 * The e-script's MedicationPrescribed > Substitutions indicator states
 * whether the prescriber allows the pharmacy to substitute (NCPDP SCRIPT
 * codes; code 1 = "Substitution Not Allowed by Prescriber", every other
 * observed code permits substitution). PioneerRx's entered side carries a
 * DAW ("Dispense As Written") checkbox (AutomationId uxDawCode — see
 * overlay Uia/FieldMap.cs). The two must agree in one direction only: if
 * the prescriber disallows substitution, DAW must be checked. There is no
 * requirement in the other direction — a pharmacist may still choose DAW
 * even when substitution is technically allowed (patient request, etc.),
 * so that combination is never flagged.
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
    return {
      status: 'green',
      reasonCode: 'substitution_allowed',
      explanation: 'Source e-prescription allows substitution — the DAW checkbox is not required to be checked.'
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
