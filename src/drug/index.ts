/**
 * Drug identity comparison.
 *
 * RxNormProvider is an INTERFACE. FixtureProvider below embeds ~20
 * synthetic-but-realistic concepts for development and tests.
 *
 * TO SWAP IN REAL DATA LATER (owner task): implement RxNormProvider
 * against the actual NLM RxNorm RRF files (RXNCONSO.RRF / RXNSAT.RRF etc,
 * or the RxNorm REST API). That requires a free UTS (UMLS Terminology
 * Services) account — https://uts.nlm.nih.gov/uts/signup-login. Once
 * that provider exists, pass it into `verify()` in place of
 * FixtureProvider; no other engine code changes.
 *
 * Verdict philosophy:
 *  - identical NDC = GREEN
 *  - same ingredient + strength + form via RxNorm, different NDC =
 *    YELLOW generic_substitution (this is routine and expected)
 *  - same product, different package size only = YELLOW pack_size
 *  - different ingredient OR strength OR form = RED
 */

export type DrugCompareStatus = 'green' | 'yellow' | 'red';

export interface DrugCompareResult {
  status: DrugCompareStatus;
  reasonCode: string;
  explanation: string;
}

export interface RxConcept {
  rxcui: string;
  ingredient: string;
  strength: string;
  doseForm: string;
  /** If this concept is a brand product, the generic ingredient it maps to. */
  brandOf?: string;
  /** Display name, e.g. "Zestril 10mg tablet". */
  name: string;
}

/**
 * Interface a real RxNorm-backed implementation must satisfy.
 * getConcept accepts either an NDC code or a free-text drug name and
 * returns the matching concept, or null if unknown.
 */
export interface RxNormProvider {
  getConcept(ndcOrName: string): RxConcept | null;
}

/**
 * ~20 synthetic-but-realistic concepts covering common brand/generic
 * pairs. rxcui values here are NOT real RxNorm CUIs — they are
 * fixture-only IDs for testing. NDCs are fixture/synthetic as well.
 */
const FIXTURE_CONCEPTS: RxConcept[] = [
  { rxcui: 'FX0001', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet', name: 'Zestril 10mg tablet', brandOf: 'lisinopril' },
  { rxcui: 'FX0002', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet', name: 'Lisinopril 10mg tablet' },
  { rxcui: 'FX0003', ingredient: 'atorvastatin', strength: '20mg', doseForm: 'tablet', name: 'Lipitor 20mg tablet', brandOf: 'atorvastatin' },
  { rxcui: 'FX0004', ingredient: 'atorvastatin', strength: '20mg', doseForm: 'tablet', name: 'Atorvastatin 20mg tablet' },
  { rxcui: 'FX0005', ingredient: 'levothyroxine', strength: '50mcg', doseForm: 'tablet', name: 'Synthroid 50mcg tablet', brandOf: 'levothyroxine' },
  { rxcui: 'FX0006', ingredient: 'levothyroxine', strength: '50mcg', doseForm: 'tablet', name: 'Levothyroxine 50mcg tablet' },
  { rxcui: 'FX0007', ingredient: 'metformin', strength: '500mg', doseForm: 'tablet', name: 'Glucophage 500mg tablet', brandOf: 'metformin' },
  { rxcui: 'FX0008', ingredient: 'metformin', strength: '500mg', doseForm: 'tablet', name: 'Metformin 500mg tablet' },
  { rxcui: 'FX0009', ingredient: 'amoxicillin', strength: '500mg', doseForm: 'capsule', name: 'Amoxicillin 500mg capsule' },
  { rxcui: 'FX0010', ingredient: 'azithromycin', strength: '250mg', doseForm: 'tablet', name: 'Azithromycin 250mg tablet' },
  { rxcui: 'FX0011', ingredient: 'amlodipine', strength: '5mg', doseForm: 'tablet', name: 'Norvasc 5mg tablet', brandOf: 'amlodipine' },
  { rxcui: 'FX0012', ingredient: 'amlodipine', strength: '5mg', doseForm: 'tablet', name: 'Amlodipine 5mg tablet' },
  { rxcui: 'FX0013', ingredient: 'metoprolol', strength: '25mg', doseForm: 'tablet', name: 'Lopressor 25mg tablet', brandOf: 'metoprolol' },
  { rxcui: 'FX0014', ingredient: 'metoprolol', strength: '25mg', doseForm: 'tablet', name: 'Metoprolol 25mg tablet' },
  { rxcui: 'FX0015', ingredient: 'omeprazole', strength: '20mg', doseForm: 'capsule', name: 'Prilosec 20mg capsule', brandOf: 'omeprazole' },
  { rxcui: 'FX0016', ingredient: 'omeprazole', strength: '20mg', doseForm: 'capsule', name: 'Omeprazole 20mg capsule' },
  { rxcui: 'FX0017', ingredient: 'sertraline', strength: '50mg', doseForm: 'tablet', name: 'Zoloft 50mg tablet', brandOf: 'sertraline' },
  { rxcui: 'FX0018', ingredient: 'sertraline', strength: '50mg', doseForm: 'tablet', name: 'Sertraline 50mg tablet' },
  { rxcui: 'FX0019', ingredient: 'albuterol', strength: '90mcg', doseForm: 'inhaler', name: 'Ventolin HFA 90mcg inhaler', brandOf: 'albuterol' },
  { rxcui: 'FX0020', ingredient: 'gabapentin', strength: '300mg', doseForm: 'capsule', name: 'Gabapentin 300mg capsule' }
];

/**
 * NDC -> concept mapping. Multiple NDCs can point at the same rxcui
 * (different labeler/package = same product). NDCs here are synthetic,
 * chosen to look like plausible 11-digit (5-4-2) NDCs.
 */
const NDC_TO_RXCUI: Record<string, string> = {
  '00071015523': 'FX0001', // Zestril 10mg, bottle of 30
  '00071015590': 'FX0001', // Zestril 10mg, bottle of 90 (different package)
  '00093715601': 'FX0002', // generic lisinopril 10mg
  '00071015601': 'FX0003', // Lipitor 20mg
  '00093715701': 'FX0004', // generic atorvastatin 20mg
  '00048110001': 'FX0005', // Synthroid 50mcg
  '00093510001': 'FX0006', // generic levothyroxine 50mcg
  '00087607001': 'FX0007', // Glucophage 500mg
  '00093715801': 'FX0008', // generic metformin 500mg
  '00093414001': 'FX0009', // amoxicillin 500mg
  '00069314001': 'FX0010', // azithromycin 250mg
  '00069315001': 'FX0011', // Norvasc 5mg
  '00093715901': 'FX0012', // generic amlodipine 5mg
  '00028008001': 'FX0013', // Lopressor 25mg
  '00093716001': 'FX0014', // generic metoprolol 25mg
  '00186507001': 'FX0015', // Prilosec 20mg
  '00093716101': 'FX0016', // generic omeprazole 20mg
  '00049494001': 'FX0017', // Zoloft 50mg
  '00093716201': 'FX0018', // generic sertraline 50mg
  '00173068201': 'FX0019', // Ventolin HFA
  '00093716301': 'FX0020' // gabapentin 300mg
};

/** Fixture-backed implementation of RxNormProvider for dev/tests. */
export class FixtureProvider implements RxNormProvider {
  getConcept(ndcOrName: string): RxConcept | null {
    const normalizedNdc = parseNdc(ndcOrName);
    if (normalizedNdc) {
      const rxcui = NDC_TO_RXCUI[normalizedNdc.normalized11];
      if (rxcui) return FIXTURE_CONCEPTS.find((c) => c.rxcui === rxcui) ?? null;
    }
    // Name lookup requires a whole-string match or a token-boundary
    // match on the concept's brand/ingredient word. A fragment like
    // "20mg tablet" must resolve to NOTHING — resolving it to the first
    // 20mg product in the table would be a false identification.
    const nameFold = ndcOrName.toLowerCase().trim();
    if (!nameFold) return null;

    const exact = FIXTURE_CONCEPTS.find((c) => c.name.toLowerCase() === nameFold);
    if (exact) return exact;

    const queryTokens = new Set(nameFold.split(/\s+/));
    // Prefer a match on the concept's leading brand/ingredient word...
    const leadMatch = FIXTURE_CONCEPTS.find((c) => {
      const lead = c.name.toLowerCase().split(' ')[0] ?? '';
      return lead.length > 0 && queryTokens.has(lead);
    });
    if (leadMatch) return leadMatch;
    // ...then fall back to the generic ingredient as a whole token.
    return FIXTURE_CONCEPTS.find((c) => queryTokens.has(c.ingredient)) ?? null;
  }

  /** Look up which NDCs are known to map to the same concept as `ndc`. */
  ndcsForConcept(rxcui: string): string[] {
    return Object.entries(NDC_TO_RXCUI)
      .filter(([, v]) => v === rxcui)
      .map(([k]) => k);
  }
}

export interface ParsedNdc {
  /** Normalized to an 11-digit 5-4-2 string, digits only. */
  normalized11: string;
  labeler: string;
  product: string;
  packageCode: string;
}

/**
 * Parse an NDC in any common 10 or 11 digit format (with or without
 * dashes) into labeler/product/package segments, normalized to the
 * standard 5-4-2 (11-digit) representation used by pharmacy systems.
 *
 * 10-digit NDCs come in three FDA configurations (4-4-2, 5-3-2, 5-4-1);
 * we detect via dash positions when present. A BARE (undelimited)
 * 10-digit NDC is genuinely ambiguous between those three layouts, so
 * we refuse to guess and return null — the drug comparison then falls
 * back to the RxNorm/name path, or a YELLOW "cannot resolve" verdict.
 * Resolving undelimited 10-digit NDCs correctly requires a labeler-code
 * length table (FDA labeler registry) — future work, documented here on
 * purpose.
 */
export function parseNdc(raw: string): ParsedNdc | null {
  const cleaned = raw.trim();
  if (!/^[0-9-]+$/.test(cleaned)) return null;

  if (cleaned.includes('-')) {
    const segments = cleaned.split('-');
    if (segments.length !== 3) return null;
    let [labeler, product, pkg] = segments as [string, string, string];
    const totalDigits = labeler.length + product.length + pkg.length;
    if (totalDigits === 10) {
      // Determine which segment needs zero-padding based on standard
      // 10-digit configurations: 4-4-2, 5-3-2, 5-4-1.
      if (labeler.length === 4) labeler = labeler.padStart(5, '0');
      else if (product.length === 3) product = product.padStart(4, '0');
      else if (pkg.length === 1) pkg = pkg.padStart(2, '0');
    } else if (totalDigits !== 11) {
      return null;
    }
    labeler = labeler.padStart(5, '0');
    product = product.padStart(4, '0');
    pkg = pkg.padStart(2, '0');
    return { normalized11: `${labeler}${product}${pkg}`, labeler, product, packageCode: pkg };
  }

  const digits = cleaned;
  if (digits.length === 11) {
    return {
      normalized11: digits,
      labeler: digits.slice(0, 5),
      product: digits.slice(5, 9),
      packageCode: digits.slice(9, 11)
    };
  }
  // A bare 10-digit NDC is ambiguous (5-4-1 vs 4-4-2 vs 5-3-2). Guessing
  // a layout risks identifying the WRONG product, which is worse than
  // not identifying one at all — return null and let the comparison
  // fall back to the RxNorm/name path (or a YELLOW verdict).
  return null;
}

export function compareDrugs(
  sourceRaw: { name?: string; ndc?: string } | null | undefined,
  enteredRaw: { name?: string; ndc?: string } | null | undefined,
  provider: RxNormProvider
): DrugCompareResult {
  const sourceEmpty = !sourceRaw || (!sourceRaw.name && !sourceRaw.ndc);
  const enteredEmpty = !enteredRaw || (!enteredRaw.name && !enteredRaw.ndc);

  if (sourceEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription did not provide a drug to compare.'
    };
  }
  if (enteredEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'No drug was entered in PioneerRx to compare against the source.'
    };
  }

  const src = sourceRaw as { name?: string; ndc?: string };
  const ent = enteredRaw as { name?: string; ndc?: string };

  const srcNdc = src.ndc ? parseNdc(src.ndc) : null;
  const entNdc = ent.ndc ? parseNdc(ent.ndc) : null;

  if (srcNdc && entNdc && srcNdc.normalized11 === entNdc.normalized11) {
    return {
      status: 'green',
      reasonCode: 'exact_match',
      explanation: 'NDC matches exactly.'
    };
  }

  const srcConcept = (srcNdc && provider.getConcept(srcNdc.normalized11)) || (src.name && provider.getConcept(src.name)) || null;
  const entConcept = (entNdc && provider.getConcept(entNdc.normalized11)) || (ent.name && provider.getConcept(ent.name)) || null;

  if (!srcConcept || !entConcept) {
    return {
      status: 'yellow',
      reasonCode: 'unknown_drug',
      explanation: 'Could not resolve one or both drugs to a known concept; needs human review.'
    };
  }

  if (srcConcept.rxcui === entConcept.rxcui) {
    // Same concept, but NDCs differ -> package size difference only.
    if (srcNdc && entNdc && srcNdc.labeler === entNdc.labeler && srcNdc.product === entNdc.product) {
      return {
        status: 'yellow',
        reasonCode: 'pack_size',
        explanation: `Same product (${srcConcept.name}), different package size only.`
      };
    }
    return {
      status: 'yellow',
      reasonCode: 'generic_substitution',
      explanation: `Same ingredient, strength, and form (${srcConcept.ingredient} ${srcConcept.strength} ${srcConcept.doseForm}) dispensed under a different NDC/brand — routine generic substitution.`
    };
  }

  if (
    srcConcept.ingredient === entConcept.ingredient &&
    srcConcept.strength === entConcept.strength &&
    srcConcept.doseForm === entConcept.doseForm
  ) {
    return {
      status: 'yellow',
      reasonCode: 'generic_substitution',
      explanation: `Same ingredient, strength, and form (${srcConcept.ingredient} ${srcConcept.strength} ${srcConcept.doseForm}), different product record — routine generic substitution.`
    };
  }

  const diffs: string[] = [];
  if (srcConcept.ingredient !== entConcept.ingredient) diffs.push(`ingredient ${srcConcept.ingredient} vs ${entConcept.ingredient}`);
  if (srcConcept.strength !== entConcept.strength) diffs.push(`strength ${srcConcept.strength} vs ${entConcept.strength}`);
  if (srcConcept.doseForm !== entConcept.doseForm) diffs.push(`form ${srcConcept.doseForm} vs ${entConcept.doseForm}`);

  return {
    status: 'red',
    reasonCode: 'drug_mismatch',
    explanation: `Drug does not match: ${diffs.join('; ')}.`
  };
}
