/**
 * Shared shape/derivation logic for the bundled local NDC dataset
 * (data/ndc-data.json[.gz]). Used by BOTH:
 *  - scripts/build-drug-data.ts (build-time, network access, produces
 *    the file)
 *  - src/drug/index.ts LocalNdcProvider (runtime, zero network, reads
 *    the file)
 *
 * Keeping the derivation in one place means the "same logic on both
 * sides" invariant can't drift.
 */

/**
 * One entry per unique product. Deliberately minimal — `rxcui` and a
 * display `name` are NOT stored; they're fully derivable from these
 * four fields (see deriveRxcui/deriveName below), and precomputing them
 * would roughly double the file size for pure redundancy.
 */
export interface LocalConcept {
  displayName: string;
  ingredient: string;
  strength: string;
  doseForm: string;
}

export interface LocalDrugData {
  generatedAt: string;
  source: string;
  concepts: LocalConcept[];
  /** normalized 11-digit package NDC -> index into `concepts` */
  ndcIndex: Record<string, number>;
  /**
   * Normalized brand_name / generic_name (via normalizeDrugNameString in
   * src/drug/index.ts, shared build+runtime) -> every `concepts` index
   * whose openFDA product record carried that name. Optional/possibly
   * absent on OLDER bundles built before this field existed — runtime
   * code must treat a missing nameIndex as "no name data available"
   * (name lookup returns null), never crash or guess.
   *
   * DELIBERATELY not deduplicated down to "distinct concepts only": the
   * same normalized name (e.g. a generic name shared by every labeler's
   * copy of the same drug, or a brand name reused across several
   * strengths) routinely maps to MANY product records. Runtime
   * (LocalNdcProvider.getConcept) is responsible for narrowing that list
   * down to a single unambiguous concept (using the strength/form stated
   * in the query) or returning null when it can't -- see that method's
   * doc for the full disambiguation/ambiguity rule.
   */
  nameIndex?: Record<string, number[]>;
  /**
   * concept.ingredient (the same normalized, semicolon-joined,
   * alphabetized ingredient-set key deriveRxcui/deriveName use) -> every
   * DISTINCT normalized dosage_form seen anywhere in the dataset for that
   * ingredient set. Answers "does lisinopril only ever come as a
   * tablet?" Purely informational/confirmatory context (see
   * LocalNdcProvider.knownFormsFor and compareDrugs) -- never used to
   * auto-green or auto-red on its own. Optional for the same
   * old-bundle-compat reason as nameIndex above.
   */
  formsByIngredient?: Record<string, string[]>;
}

/**
 * Derive an approximate generic-equivalence key from a concept's
 * normalized ingredient-set + per-ingredient strengths + dosage form.
 *
 * NOT a real RxNorm CUI — openFDA's NDC directory doesn't carry a
 * reliable single rxcui per product (the `openfda.rxcui` field is
 * present on some records, absent or multi-valued on others). This key
 * is a same-shape stand-in used purely as the "these two products are
 * the same underlying drug" signal that RxConcept.rxcui already drives
 * in the engine's compareDrugs() (see src/drug/index.ts): two concepts
 * with the same key are treated as candidates for
 * generic_substitution/pack_size, exactly like two fixture rows sharing
 * an rxcui were.
 *
 * FOLLOW-ON (not done here, flagged on purpose): precise RxNorm-rxcui
 * equivalence needs the actual RxNorm RRF files or REST API, which
 * requires a free UMLS/UTS account (https://uts.nlm.nih.gov/uts/signup-login).
 * That would replace this approximation with real SCD-level concept
 * matching (e.g. would not be fooled by an ingredient-name spelling
 * variant this key treats as distinct).
 */
export function deriveRxcui(concept: LocalConcept): string {
  return `GX:${concept.ingredient}|${concept.strength}|${concept.doseForm}`;
}

export function deriveName(concept: LocalConcept): string {
  return `${concept.displayName} ${concept.strength} ${concept.doseForm}`;
}
