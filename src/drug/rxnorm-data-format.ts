/**
 * Shared shape for the bundled RxNorm dataset (data/rxnorm-data.json.gz).
 * Used by BOTH:
 *  - scripts/build-rxnorm-data.ts (build-time, network access, produces
 *    the file from NLM's public "RxNorm Current Prescribable Content"
 *    monthly release)
 *  - src/drug/rxnorm.ts RxNormDataProvider (runtime, zero network, reads
 *    the file)
 *
 * WHY THIS EXISTS (see src/drug/index.ts header, "TO GET PRECISE RXNORM
 * EQUIVALENCE LATER"): LocalNdcProvider's rxcui is a SYNTHETIC,
 * derived-from-openFDA-fields stand-in (see local-data-format.ts
 * deriveRxcui) — it is NOT a real RxNorm CUI. This file's `rxcui`/
 * `scdRxcui` fields, by contrast, ARE real RXCUIs from NLM's RxNorm. The
 * two id spaces are never compared directly anywhere in this codebase —
 * see rxnorm.ts's comparison helpers, which compare the DERIVED
 * ingredient/strength/doseForm triple instead, normalized with the SAME
 * functions (normalizeIngredientName/normalizeStrength from
 * scripts/build-drug-data.ts) openFDA data already uses, specifically so
 * the two differently-sourced triples are comparable despite the id
 * spaces differing.
 *
 * SCOPE (v1, documented gap): only TTY=SCD (Semantic Clinical Drug) and
 * TTY=SBD (Semantic Branded Drug) concepts are built. GPCK/BPCK
 * (multi-component combination PACKS, e.g. a 28-day oral contraceptive
 * pack with two distinct tablet phases) are deliberately NOT parsed —
 * their RXNCONSO STR grammar is a nested "{n (component) / n (component)}
 * Pack [...]" shape that would need materially different parsing logic,
 * and combination packs are a small minority of real prescriptions. See
 * scripts/build-rxnorm-data.ts's buildRxNormConcept for where this is
 * enforced (any TTY other than SCD/SBD is skipped, never guessed at).
 */

export interface RxNormConcept {
  /** Real RxNorm RXCUI for this concept (SCD or SBD). */
  rxcui: string;
  tty: 'SCD' | 'SBD';
  /**
   * For an SBD (branded) concept only: the RXCUI of the underlying SCD
   * (generic clinical drug) it's a tradename of, when RXNREL carries a
   * resolvable has_tradename relationship to a concept that is itself a
   * TTY=SCD in this same release (see deriveScdRxcui in the build
   * script). Absent when SCD (an SCD IS already the generic concept) or
   * when no such relationship could be confirmed — never guessed.
   */
  scdRxcui?: string;
  /** The original RXNCONSO STR for this RXCUI, unmodified — e.g.
   * "lisinopril 10 MG Oral Tablet" or "lisinopril 10 MG Oral Tablet
   * [Zestril]". Shown to a human in a GREEN explanation; never used for
   * programmatic comparison (see ingredient/strength/doseForm below for
   * that). */
  displayName: string;
  /**
   * Normalized, semicolon-joined, alphabetized ingredient-name set —
   * SAME shape as LocalConcept.ingredient (see local-data-format.ts and
   * buildConcept in scripts/build-drug-data.ts): each component
   * normalized via normalizeIngredientName, sorted, joined with ';'.
   */
  ingredient: string;
  /** Per-ingredient strength, normalized via normalizeStrength, joined
   * with ';' in the SAME order as `ingredient`'s components. */
  strength: string;
  /**
   * The matched RxNorm Dose Form (TTY=DF) vocabulary string, lowercased
   * verbatim (e.g. "extended release oral tablet") — NOT the same
   * vocabulary/word-order openFDA's dosage_form uses (e.g. "tablet,
   * extended release"), so this is never compared by string equality;
   * see doseFormsEquivalent in src/drug/rxnorm.ts for the token-set
   * comparison that reconciles the two vocabularies.
   */
  doseForm: string;
}

export interface RxNormData {
  generatedAt: string;
  /** RxNorm release identifier, e.g. "RxNorm_full_prescribe_08032026". */
  source: string;
  /**
   * Required NLM attribution + build-date disclosure (RxNorm is public
   * domain; NLM's terms ask that a build/release date be disclosed since
   * the data changes monthly — see README.md's RxNorm section and
   * https://www.nlm.nih.gov/research/umls/rxnorm/docs/prescribe.html).
   */
  attribution: string;
  concepts: RxNormConcept[];
  /** normalized 11-digit package NDC -> index into `concepts`. Only
   * concepts with at least one matched NDC are present in `concepts` at
   * all (an SCD/SBD with zero NDC attributes in this release is useless
   * for written-NDC lookup and would only bloat the file — but see
   * scdDisplayNames below, which is NOT limited this way). */
  ndcIndex: Record<string, number>;
  /**
   * EVERY known SCD rxcui in this release -> its displayName, regardless
   * of whether that SCD has its own NDC (an SBD's scdRxcui target
   * routinely has none — branded packaging carries the NDC, not the
   * bare generic concept). Lets a GREEN explanation name the generic
   * concept even when scdRxcui isn't itself reachable via ndcIndex.
   */
  scdDisplayNames: Record<string, string>;
}
