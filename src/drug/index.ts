/**
 * Drug identity comparison.
 *
 * RxNormProvider is an INTERFACE.
 *  - FixtureProvider: ~20 synthetic-but-realistic concepts, for tests
 *    and dev (fixture-only rxcui/NDC values, never real).
 *  - LocalNdcProvider: the REAL provider, backed by a local, offline,
 *    pre-built dataset derived from the public openFDA NDC directory
 *    (see data/ndc-data.json.gz + scripts/build-drug-data.ts). This is
 *    what src/cli.ts wires up. It makes ZERO network calls at lookup
 *    time — the dataset is downloaded/transformed by
 *    scripts/build-drug-data.ts (a maintainer-run, build-time-only
 *    script), committed to the repo, and loaded into an in-memory Map
 *    once per process. This preserves the HIPAA local-only guarantee:
 *    verifying a prescription never touches the network.
 *
 * GENERIC-EQUIVALENCE APPROXIMATION (LocalNdcProvider only): openFDA's
 * NDC directory doesn't carry a reliable single RxNorm CUI per product,
 * so LocalNdcProvider derives an approximate equivalence key from the
 * normalized ingredient-set + per-ingredient strength + dosage form
 * (see deriveRxcui in src/drug/local-data-format.ts) and uses that in
 * RxConcept.rxcui. This is intentionally NOT real RxNorm-rxcui
 * equivalence — it will, for example, treat "atorvastatin" and
 * "atorvastatin calcium trihydrate" as different ingredients (they are
 * different strings) even though they're the same drug via different
 * salt forms. That's a real limitation, not a bug: it can only ever
 * fail toward MORE yellow (drug_mismatch/unknown), never a false green,
 * so it's safe for this engine's philosophy even though it's coarser
 * than real RxNorm.
 *
 * TO GET PRECISE RXNORM EQUIVALENCE LATER (owner task, follow-on):
 * implement a provider against the actual NLM RxNorm RRF files
 * (RXNCONSO.RRF / RXNSAT.RRF etc, or the RxNorm REST API — note the
 * REST API would need a build-time-only fetch too, same offline rule
 * as above). That requires a free UTS (UMLS Terminology Services)
 * account — https://uts.nlm.nih.gov/uts/signup-login. Once that
 * provider exists, pass it into `verify()` in place of
 * LocalNdcProvider; no other engine code changes.
 *
 * Verdict philosophy:
 *  - identical NDC = GREEN
 *  - same ingredient + strength + form via RxNorm, different NDC =
 *    YELLOW generic_substitution (this is routine and expected)
 *  - same product, different package size only = YELLOW pack_size
 *  - different ingredient OR strength OR form = RED
 */

import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { deriveRxcui, deriveName, type LocalConcept, type LocalDrugData } from './local-data-format.js';

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
  /**
   * OPTIONAL: every distinct normalized dosage form known for a given
   * ingredient key (RxConcept.ingredient's normalized, semicolon-joined
   * value — see LocalConcept.ingredient / deriveRxcui). Returns null
   * when the provider doesn't track this (e.g. FixtureProvider) or the
   * ingredient is unknown. Purely informational confirmation context —
   * see compareDrugs' use of it — never a basis for a verdict on its
   * own.
   */
  knownFormsFor?(ingredientKey: string): string[] | null;
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

/**
 * Fixture-backed implementation of RxNormProvider for dev/tests ONLY --
 * DO NOT wire this into any live/production code path. src/cli.ts wires
 * up LocalNdcProvider (the real, openFDA-backed provider) for that; this
 * class exists purely so tests don't need the ~130k-concept dataset.
 *
 * Reviewer round 1 (drug-name-fallback review), confirmed live risk: this
 * class's NAME lookup (see getConcept below) is intentionally much
 * cruder than LocalNdcProvider.resolveConceptByName -- it matches on a
 * bare leading brand/ingredient word and IGNORES stated strength and
 * salt entirely. That's fine for this file's own unit tests (which
 * either pick names precisely to exploit or avoid that behavior) but
 * would be actively unsafe wired up for real verification: e.g.
 * "Metoprolol Tartrate 50 Mg Tablet" and "Metoprolol Succinate 50 Mg
 * Tablet" both leading-word-match this fixture's single "metoprolol"
 * concept and would false-GREEN via the concept_match path in
 * compareDrugs below, silently blurring two clinically different,
 * non-interchangeable products. LocalNdcProvider's resolveConceptByName
 * does NOT have this gap (it narrows candidates by stated strength/
 * release-qualifier before ever calling something unambiguous) -- see
 * that function's own doc.
 */
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

interface LoadedDataset {
  concepts: LocalConcept[];
  ndcIndex: Map<string, number>;
  /** Absent (empty Map) on an older bundle built before name lookup existed. */
  nameIndex: Map<string, number[]>;
  /** Absent (empty Map) on an older bundle built before this field existed. */
  formsByIngredient: Map<string, string[]>;
}

/**
 * Loaded once per process and cached at module scope — the dataset is
 * ~130k concepts / ~250k NDCs, and every LocalNdcProvider instance
 * (e.g. one per test) should share it rather than re-parsing gzipped
 * JSON repeatedly.
 */
let cachedDataset: LoadedDataset | null = null;

function defaultDataPath(): string {
  const here = path.dirname(fileURLToPath(import.meta.url));
  // here = <repo>/src/drug (dev, via tsx) or <repo>/dist/drug (built) —
  // both are two directories below the repo root, so the relative path
  // back up to data/ is the same in both cases.
  return path.join(here, '..', '..', 'data', 'ndc-data.json.gz');
}

function loadDataset(dataPath: string): LoadedDataset {
  const gz = readFileSync(dataPath);
  const json = gunzipSync(gz).toString('utf8');
  const parsed = JSON.parse(json) as LocalDrugData;
  const ndcIndex = new Map<string, number>(Object.entries(parsed.ndcIndex));
  // nameIndex/formsByIngredient are OPTIONAL on LocalDrugData (a bundle
  // built before this feature existed won't have them) — default to
  // empty Maps rather than throwing, so an unrebuilt bundle keeps
  // working exactly as before (name lookup just always misses, same as
  // today's shipped behavior).
  const nameIndex = new Map<string, number[]>(Object.entries(parsed.nameIndex ?? {}));
  const formsByIngredient = new Map<string, string[]>(Object.entries(parsed.formsByIngredient ?? {}));
  return { concepts: parsed.concepts, ndcIndex, nameIndex, formsByIngredient };
}

/**
 * Max number of leading whitespace-separated tokens tried as a
 * candidate nameIndex key (see resolveConceptByName below). Real
 * brand/generic names in the dataset are almost always 1-2 words
 * ("cariprazine", "Dextroamphetamine Saccharate"); a handful of combo
 * generic names run longer ("amphetamine aspartate monohydrate..."), so
 * 4 gives headroom without scanning the whole (possibly long) query
 * string as false candidate keys.
 */
const MAX_NAME_KEY_TOKENS = 4;

/**
 * Release-RATE qualifier tokens this NAME-resolution safety check
 * distinguishes: SR/XL/ER/IR/CR/DR. Same abbreviation vocabulary as
 * RELEASE_ABBREVS/RELEASE_PHRASE_FOLDS further down this file (which
 * FOLD a spelled-out phrase down to its abbreviation for the
 * name-identity fast path), plus XL — which this codebase deliberately
 * does NOT fold to/from ER anywhere (see RELEASE_PHRASE_FOLDS' own doc:
 * "the codebase does not treat XL/XR as equivalent to ER anywhere
 * else... this is a NEW equivalence class this branch is not authorized
 * to introduce"). This set is used to DETECT, never to fold/equate:
 * openFDA's `dosage_form` field does not distinguish SR from XL (both
 * normalize to the same "tablet, extended release"-style text), so
 * ingredient+strength+doseForm equality alone is NOT enough evidence
 * that two name-resolved concepts are really the same product — see
 * resolveConceptByName and compareDrugs' qualifierConflict guard below,
 * both added after a confirmed live false GREEN: "Bupropion SR 300 MG"
 * vs "Bupropion XL 300 MG" resolved to the same derived concept because
 * the bare "bupropion" nameIndex key (reached when no "bupropion sr"/
 * "bupropion xl" key exists) carries no qualifier text at all, and SR
 * vs XL 300mg tablets share an identical openFDA dosage_form.
 */
const RELEASE_QUALIFIER_TOKENS = new Set(['er', 'sr', 'cr', 'dr', 'ir', 'xl']);

/**
 * Extract the release-RATE qualifier stated in a free-text drug name —
 * after the same normalizeDrugNameString folding everything else in
 * this file uses, so a spelled-out phrase like "extended release"
 * already reads as the token "er" by the time this runs (see
 * RELEASE_PHRASE_FOLDS) — or null if none is stated. See
 * RELEASE_QUALIFIER_TOKENS' doc for why this exists and both call
 * sites (resolveConceptByName, compareDrugs) for how it's used.
 */
function extractReleaseQualifier(rawName: string): string | null {
  const normalized = normalizeDrugNameString(rawName);
  for (const tok of normalized.split(' ')) {
    if (RELEASE_QUALIFIER_TOKENS.has(tok)) return tok;
  }
  return null;
}

/**
 * Resolve a free-text drug name to AT MOST ONE unambiguous concept, or
 * null. Conservative-by-design at every stage (a doubtful case returns
 * null, never a guess):
 *
 * 1. FIND CANDIDATES: normalize the full query the same way
 *    normalizeDrugNameString folds everything else in this engine, then
 *    try the LEADING 1..MAX_NAME_KEY_TOKENS-word prefix (longest first)
 *    against nameIndex. Brand/generic/ingredient names in this engine's
 *    inputs always lead the string (strength/form/route follow) — see
 *    FixtureProvider's own "leading word" convention above and
 *    DOSAGE_FORM_WORDS' doc — so this mirrors existing, already-tested
 *    behavior rather than inventing a new matching philosophy. The
 *    FIRST (longest) prefix that hits the index wins; no match at any
 *    length -> null (today's exact behavior, unchanged).
 * 2. NARROW by a strength STATED in the raw query, when present (same
 *    functions compareDrugs' own strength cross-check already uses).
 *    A stated strength that matches none of the candidates -> null
 *    (never fall back to the unnarrowed set and risk a wrong strength).
 * 3. NARROW by a release-RATE QUALIFIER stated in the raw query
 *    (RELEASE_QUALIFIER_TOKENS), when present: keep only candidates
 *    whose OWN displayName also states that same qualifier. This is
 *    what closes the Bupropion SR/XL gap above — a candidate reached
 *    via a qualifier-blind key (like bare "bupropion") never carries
 *    "sr" or "xl" in its own displayName, so a query that states one
 *    correctly finds ZERO confirming candidates and misses rather than
 *    guessing. A query that states NO qualifier is unaffected (this
 *    step is skipped entirely) — e.g. the Vraylar/cariprazine
 *    acceptance pair, which never mentions a release rate.
 * 4. DISAMBIGUATE: collapse whatever candidates survive steps 2-3 to
 *    their DISTINCT (ingredient, strength, doseForm) triples:
 *      - exactly one distinct triple -> unambiguous, return it (even if
 *        several duplicate product records share it — that's expected,
 *        e.g. many labelers of the same generic).
 *      - zero or more-than-one distinct triple -> ambiguous or an
 *        unconfirmed strength/qualifier -> null. Per this feature's
 *        IRON RULE, a miss/ambiguous hit here must never surface as
 *        anything but "unresolved" to the caller.
 */
function resolveConceptByName(
  rawName: string,
  concepts: LocalConcept[],
  nameIndex: Map<string, number[]>
): LocalConcept | null {
  const normalized = normalizeDrugNameString(rawName);
  if (!normalized) return null;
  const tokens = normalized.split(' ').filter(Boolean);
  if (tokens.length === 0) return null;

  let candidateIndices: number[] | undefined;
  for (let len = Math.min(MAX_NAME_KEY_TOKENS, tokens.length); len >= 1; len--) {
    const key = tokens.slice(0, len).join(' ');
    const hit = nameIndex.get(key);
    if (hit && hit.length > 0) {
      candidateIndices = hit;
      break;
    }
  }
  if (!candidateIndices) return null;

  let candidates = candidateIndices.map((i) => concepts[i]).filter((c): c is LocalConcept => c !== undefined);
  if (candidates.length === 0) return null;

  // Narrow by a strength STATED in the raw query, when present. Reuses
  // the same strength-normalization the rest of this file already
  // trusts (extractStatedConcentrationStrength/extractStatedStrength),
  // so "1.5mg" here means the same thing "1.5mg" means in
  // LocalConcept.strength (see normalizeStrength in
  // scripts/build-drug-data.ts — same compact digit+unit shape, no
  // space).
  const statedConcentration = extractStatedConcentrationStrength(rawName);
  const statedSingle = statedConcentration ? null : extractStatedStrength(rawName);
  const statedStrength = statedConcentration ?? statedSingle;
  if (statedStrength) {
    const narrowed = candidates.filter((c) => c.strength === statedStrength);
    // Only trust the narrowed set when it actually narrowed to
    // something — if the stated strength matches NONE of the
    // candidates, that's a genuine signal something's off (wrong drug,
    // or a strength this dataset doesn't have on record for that name);
    // falling back to the unnarrowed candidate set here would risk
    // silently picking a WRONG strength, which is exactly the false
    // positive this whole feature must never produce. Treat it as a
    // miss instead.
    if (narrowed.length === 0) return null;
    candidates = narrowed;
  }

  // Narrow by a release-RATE QUALIFIER stated in the raw query, when
  // present — see RELEASE_QUALIFIER_TOKENS' doc for the live bug this
  // closes (Bupropion SR vs XL). A candidate only counts as confirming
  // the query's stated qualifier when the candidate's OWN displayName
  // states that SAME qualifier; a candidate with no qualifier at all in
  // its name (e.g. a bare "bupropion" record) never confirms one.
  const statedQualifier = extractReleaseQualifier(rawName);
  if (statedQualifier) {
    const qualifierConfirmed = candidates.filter((c) => extractReleaseQualifier(c.displayName) === statedQualifier);
    if (qualifierConfirmed.length === 0) return null;
    candidates = qualifierConfirmed;
  }

  const distinctKey = (c: LocalConcept): string => `${c.ingredient}|${c.strength}|${c.doseForm}`;
  const distinct = new Map<string, LocalConcept>();
  for (const c of candidates) {
    distinct.set(distinctKey(c), c);
  }
  if (distinct.size !== 1) return null; // ambiguous (or, with 0, unreachable) -> miss, never a guess

  return [...distinct.values()][0] as LocalConcept;
}

/**
 * REAL, LOCAL, OFFLINE drug provider backed by the bundled openFDA NDC
 * dataset (data/ndc-data.json.gz). Reads the file ONCE into an
 * in-memory Map at construction (or on first use, cached at module
 * scope) — every `getConcept` call afterward is a plain Map lookup.
 * ZERO network calls happen anywhere in this class or its callers.
 *
 * NAME-BASED lookup (getConcept called with a free-text drug name, not
 * an NDC): resolves via the openFDA brand_name/generic_name -> concept
 * index built at build time (nameIndex, see scripts/build-drug-data.ts)
 * and narrowed at lookup time by resolveConceptByName above. This is
 * still a HEURISTIC, not real RxNorm-CUI resolution — see this file's
 * header for the same caveat that already applies to NDC-based
 * resolution. Its safety rests entirely on resolveConceptByName's
 * conservative-by-design behavior: any doubt (no hit, multiple distinct
 * ingredient/strength/form candidates, a stated strength that matches
 * none of them) resolves to null, exactly the same `unknown_drug`
 * yellow this engine has always fallen back to for an unresolvable
 * drug — never a guess, and per compareDrugs' handling of it (see that
 * function's IRON RULE comment), never independently escalated to red.
 */
export class LocalNdcProvider implements RxNormProvider {
  private readonly concepts: LocalConcept[];
  private readonly ndcIndex: Map<string, number>;
  private readonly nameIndex: Map<string, number[]>;
  private readonly formsByIngredient: Map<string, string[]>;

  constructor(dataPath?: string) {
    const isDefaultPath = dataPath === undefined;
    const resolvedPath = dataPath ?? defaultDataPath();
    // Only share the module-level cache for the default path, so a
    // test that points at a custom fixture file never sees stale data
    // from a previous default-path load (and vice versa).
    const dataset = isDefaultPath && cachedDataset ? cachedDataset : loadDataset(resolvedPath);
    if (isDefaultPath) cachedDataset = dataset;
    this.concepts = dataset.concepts;
    this.ndcIndex = dataset.ndcIndex;
    this.nameIndex = dataset.nameIndex;
    this.formsByIngredient = dataset.formsByIngredient;
  }

  getConcept(ndcOrName: string): RxConcept | null {
    const parsed = parseNdc(ndcOrName);
    const concept = parsed
      ? this.conceptByNdc(parsed.normalized11)
      : resolveConceptByName(ndcOrName, this.concepts, this.nameIndex);
    if (!concept) return null;
    return {
      rxcui: deriveRxcui(concept),
      name: deriveName(concept),
      ingredient: concept.ingredient,
      strength: concept.strength,
      doseForm: concept.doseForm
    };
  }

  private conceptByNdc(normalized11: string): LocalConcept | null {
    const index = this.ndcIndex.get(normalized11);
    if (index === undefined) return null;
    return this.concepts[index] ?? null;
  }

  /** See RxNormProvider.knownFormsFor's doc. */
  knownFormsFor(ingredientKey: string): string[] | null {
    return this.formsByIngredient.get(ingredientKey) ?? null;
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

/**
 * Extract a strength token ("20mg", "50mcg", "2.5 mg" -> "2.5mg") stated
 * in a free-text drug name. Returns null when no strength is stated.
 */
export function extractStatedStrength(name: string): string | null {
  const m = /(\d+(?:\.\d+)?)\s*(mcg|mg|ml|g|units?)\b/i.exec(name);
  if (!m) return null;
  const unit = (m[2] ?? '').toLowerCase().replace(/^units$/, 'unit');
  return `${m[1]}${unit}`;
}

/**
 * Round 6, fix 2: OCR confusables scoped NARROWLY to the immediate
 * vicinity of a compound concentration strength ("12.5 mg/0.5 mL"), never
 * applied to the name string generally:
 *  - a unit word (mg/mcg/ml/g/unit(s)) immediately followed by "I" or
 *    "1" where "/" belongs ("MGI0.5" / "MGIO.5" -> "MG/0.5" / "MG/O.5"),
 *    only when what follows looks like the start of the second number
 *    (a digit, or an "o." that itself needs the next fix below);
 *  - a bare "O" standing in for "0" immediately before a decimal point
 *    that's itself followed by a digit ("O.5" -> "0.5") — the classic
 *    OCR zero/letter-O confusion, scoped to this exact decimal-point-
 *    adjacent shape.
 * Field report: source e-script text "12.5 MGIO.5 ML ..." (intended:
 * "12.5 MG/0.5 ML") — see extractStatedConcentrationStrength's doc for
 * why the fix has to happen before that regex runs, not by fuzzing the
 * strength-matching regex itself.
 */
function fixStrengthOcrConfusables(s: string): string {
  let out = s.replace(/\b(mcg|mg|ml|g|units?)[i1](?=\d|o\.)/gi, '$1/');
  out = out.replace(/\bo(?=\.\d)/gi, '0');
  return out;
}

const CONCENTRATION_STRENGTH_RE =
  /(\d+(?:\.\d+)?)\s*(mcg|mg|ml|g|units?)\s*\/\s*(\d+(?:\.\d+)?)\s*(mcg|mg|ml|g|units?)\b/i;

/**
 * True when a free-text drug name STATES (or, per fixStrengthOcrConfusables,
 * appears to have intended to state) a compound concentration strength —
 * a literal "/" concentration notation, or the specific unit+I/1-glued
 * OCR shape fixStrengthOcrConfusables targets. Used by compareDrugs to
 * decide when the plain single-value extractStatedStrength cross-check
 * is unsafe to fall back to (see the safety rule on
 * extractStatedConcentrationStrength).
 */
function hasConcentrationSignal(name: string): boolean {
  return /\//.test(name) || /\b(mcg|mg|ml|g|units?)[i1](?=\d|o\.)/i.test(name);
}

/**
 * Extract a compound CONCENTRATION strength ("12.5 mg/0.5 mL" -> one
 * "12.5mg/0.5ml" value, not two independently-competing single
 * strengths) from a free-text drug name, tolerating the narrow OCR
 * confusables in fixStrengthOcrConfusables. Field report (round 6, fix
 * 2): "ZEPBOUND 12.5 MGIO.5 ML SUBCUTANEOUS PEN INJECTOR" — the OLD
 * single-value extractStatedStrength regex, run directly against that
 * garbled text, skipped past the unparseable "MGIO.5" fragment entirely
 * and grabbed the isolated "5" + "ML" left over from "...IO.5 ML..." as
 * if "5ml" were the whole stated strength — producing a confirmed false
 * RED against a correctly-matching "12.5 Mg/0.5 Ml" entry.
 *
 * SAFETY RULE: returns null — "unparseable", never a guessed value —
 * whenever a confident compound match can't be found even after the
 * scoped confusable fixes. Callers must never derive a RED verdict from
 * a null/partial result here; a RED requires two CLEANLY parsed,
 * genuinely different compound strengths on both sides (see compareDrugs'
 * use of this alongside hasConcentrationSignal).
 */
export function extractStatedConcentrationStrength(name: string): string | null {
  const fixed = fixStrengthOcrConfusables(name);
  const m = CONCENTRATION_STRENGTH_RE.exec(fixed);
  if (!m) return null;
  const unit1 = (m[2] ?? '').toLowerCase().replace(/^units$/, 'unit');
  const unit2 = (m[4] ?? '').toLowerCase().replace(/^units$/, 'unit');
  return `${m[1]}${unit1}/${m[3]}${unit2}`;
}

/**
 * Common dosage-FORM word variants -> canonical form word. Applied as a
 * whole-token replacement (never a substring replace) so e.g. "cap" in
 * "captopril" is never touched. Deliberately only covers the handful of
 * abbreviations pharmacy systems actually produce (PioneerRx free-text
 * entry vs an e-script's spelled-out form) — release qualifiers like
 * ER/XR/CR are left as bare tokens here (they already compare equal
 * to each other via the case-folding above when BOTH sides already use
 * the same abbreviation). Spelled-out release-qualifier PHRASES
 * ("extended release" etc) ARE folded down to their abbreviation, but
 * by RELEASE_PHRASE_FOLDS below (a phrase-level regex pass, since a
 * whole-token map can't match two words) — see that constant's doc for
 * why the direction is phrase -> abbreviation only, never the reverse.
 */
const DOSAGE_FORM_WORDS: Record<string, string> = {
  tab: 'tablet', tabs: 'tablet', tablets: 'tablet',
  cap: 'capsule', caps: 'capsule', capsules: 'capsule',
  sol: 'solution', soln: 'solution',
  susp: 'suspension',
  // Bug 5 (round 3, W-T-round3): live report — entered "oint", source
  // "ointment", not recognized as the same form. Deliberately excludes
  // bare "cr" for cream ("crm" is unambiguous, added below) — "CR" is
  // ALREADY a protected release-qualifier abbreviation elsewhere in this
  // engine's philosophy (controlled-release, e.g. "Diltiazem CR" — see
  // this function's own doc above), so folding it to "cream" too would
  // risk conflating a release profile with a dosage form; per the branch
  // brief's own "only fold unambiguous abbreviations" instruction, it's
  // left out.
  oint: 'ointment', ung: 'ointment',
  crm: 'cream',
  supp: 'suppository',
  inj: 'injection',
  gtt: 'drops'
  // REVIEW FIX (non-blocking finding, round 3): "lot" -> "lotion" was
  // removed. "Lot"/"lot" routinely appears as a lot/batch-number token
  // that bleeds into a free-text name field on either side (not just a
  // dosage form abbreviation) — folding it here feeds the PRIMARY
  // name_identity_match GREEN path (normalizeDrugNameString is compared
  // for exact equality there), so a stray "Lot" token folding to
  // "lotion" on one side while the other side coincidentally has a real
  // "lotion" dosage form elsewhere in its text risked a false
  // name-identity match. The field report that motivated this round was
  // specifically "oint" vs "ointment" — no lotion-abbreviation report
  // exists — so this is dropped rather than guessed at.
};

/**
 * Release-qualifier PHRASE -> canonical abbreviation. Field report:
 * "Metoprolol Succinate ER 50 mg" (entered) vs "Metoprolol Succinate
 * Extended Release 50 mg" (e-script) was flagged as a mismatch —
 * same drug, one side spells the qualifier out. Each entry matches the
 * two words separated by whitespace OR a hyphen ("extended release" and
 * "extended-release" both fold), case-insensitively (input is already
 * lowercased by the time this runs).
 *
 * Direction is ONE-WAY on purpose: an explicit phrase folds DOWN to its
 * abbreviation (unambiguous — "extended release" only ever means ER).
 * The reverse is never done — a bare "er"/"cr"/etc. token is NOT
 * expanded up to a phrase, because a bare abbreviation is ambiguous
 * (folding "er" up would require guessing which release word was
 * meant, and could collide with an unrelated token). This is also why
 * XL/XR are NOT included here: the codebase does not treat XL/XR as
 * equivalent to ER anywhere else (grepped — no existing XL/XR<->ER
 * equivalence), so this is a NEW equivalence class this branch is not
 * authorized to introduce; only the explicit-phrase-to-abbreviation
 * folds the bug report asked for are added. XL/XR/ER stay untouched as
 * bare tokens, same as before this change.
 */
const RELEASE_PHRASE_FOLDS: Array<[RegExp, string]> = [
  [/\bextended[\s-]+release\b/g, 'er'],
  [/\bsustained[\s-]+release\b/g, 'sr'],
  [/\bcontrolled[\s-]+release\b/g, 'cr'],
  [/\bdelayed[\s-]+release\b/g, 'dr'],
  [/\bimmediate[\s-]+release\b/g, 'ir']
];

/**
 * Release-qualifier tokens that can legitimately appear TWICE in one
 * name string: openFDA-style official labeling routinely states the
 * qualifier once as the short form attached to the ingredient ("...ER
 * 30 MG...") AND again spelled out as part of the dosage-form
 * description ("...CAPSULE, EXTENDED RELEASE..."), which
 * RELEASE_PHRASE_FOLDS above also folds down to "er". Both occurrences
 * describe the SAME release profile, so after folding, keep only the
 * first occurrence of EACH DISTINCT qualifier value — otherwise a name
 * with the qualifier stated once (typical PioneerRx free-text entry)
 * would never string-match a name that states it twice (typical
 * e-script/openFDA labeling), even though they're the same drug.
 *
 * REVIEW FIX (confirmed false GREEN, fleet-wide — not amphetamine
 * specific): the dedup must track EACH qualifier VALUE seen, not just
 * "have we seen any qualifier yet". A single boolean silently dropped a
 * genuinely DIFFERENT second qualifier too — e.g. "...ER 180 MG
 * Capsule, Extended Release Delayed Release" folds to tokens
 * "er ... er dr"; a single-boolean dedupe drops the "dr" (wrong — ER
 * and DR are different release profiles), collapsing it to the same
 * string as an ER-only product and producing a false name-identity
 * GREEN between two actually-different drugs.
 */
const RELEASE_ABBREVS = new Set(['er', 'sr', 'cr', 'dr', 'ir']);

function dedupeReleaseAbbrevs(spaced: string): string {
  const seenValues = new Set<string>();
  return spaced
    .split(' ')
    .filter((tok) => {
      if (!RELEASE_ABBREVS.has(tok)) return true;
      if (seenValues.has(tok)) return false;
      seenValues.add(tok);
      return true;
    })
    .join(' ');
}

/**
 * Extract a stated release-duration token ("24 hour" / "12 hour", as in
 * "...CAPSULE EXTENDED RELEASE 24 HOUR") from a RAW (non-normalized)
 * drug name. Returns the number of hours, or null if none is stated.
 * Mirrors extractStatedStrength below: used to detect a genuine
 * CONTRADICTION (both sides state a duration and it differs) before the
 * name-identity fast path in compareDrugs, since normalizeDrugNameString
 * folds the duration phrase away entirely (see foldDurationHours) and
 * would otherwise let two differently-timed products silently match.
 */
export function extractStatedDurationHours(name: string): number | null {
  const m = /\b(\d+)\s*hour\b/i.exec(name);
  return m ? Number(m[1]) : null;
}

/**
 * Fold away a stated "N hour" release-duration phrase for the identity
 * comparison string. Safe to do unconditionally here ONLY because
 * compareDrugs separately blocks the name-identity fast path whenever
 * both sides state a DIFFERENT duration (see extractStatedDurationHours)
 * — this function alone cannot tell "one side silent" apart from
 * "both sides differ", so it must never be the sole gate.
 */
function foldDurationHours(spaced: string): string {
  return spaced.replace(/\b\d+\s*hour\b/g, '').replace(/\s+/g, ' ').trim();
}

/**
 * Round 6, fix 3: strip a trailing strength restatement that DUPLICATES
 * the strength already stated earlier in the same name string — field
 * report: e-script text restates the strength again after the dosage
 * form ("ELIQUIS 5 MG TABLET 5 mg"), which PioneerRx's entry naturally
 * doesn't repeat ("Eliquis 5 Mg Tablet"), so the two names never
 * normalized-string-matched and fell through to unknown_drug yellow.
 *
 * Deliberately requires the duplicate to be an EXACT match of the
 * earliest-stated strength AND to be the string's actual trailing
 * token(s) — a trailing strength that CONTRADICTS the earlier one
 * ("TABLET 10 mg" after "5 MG" stated earlier) is left untouched here on
 * purpose: that's a genuine restated-strength contradiction, and must
 * still surface via the existing extractStatedStrength-based
 * stated-strength-mismatch RED check in compareDrugs (which reads the
 * FIRST stated strength on each side) rather than being silently erased
 * by this fold.
 *
 * Operates on `spaced` (this function runs after normalizeDrugNameString
 * has already inserted a space between a glued digit+unit, e.g. "5mg" ->
 * "5 mg") so every strength token here is already in the consistent
 * "digit unit" shape. Never touches any other word in the name — e.g. a
 * salt suffix like "dihydrochloride" sitting between the two strength
 * occurrences is left completely alone, only the trailing duplicate
 * strength token(s) are removed.
 */
function foldTrailingDuplicateStrength(spaced: string): string {
  const strengthRe = /\b(\d+(?:\.\d+)?)\s+(mcg|mg|ml|g|units?)\b/g;
  const matches = [...spaced.matchAll(strengthRe)];
  if (matches.length < 2) return spaced;

  const first = matches[0] as RegExpMatchArray;
  const last = matches[matches.length - 1] as RegExpMatchArray;
  const firstVal = `${first[1]}${first[2]}`;
  const lastVal = `${last[1]}${last[2]}`;
  if (firstVal !== lastVal) return spaced; // contradiction — never fold, leave both in place

  // Only strip when the duplicate is genuinely the END of the string (a
  // restatement tacked on after the form word) — not a strength mentioned
  // mid-name that happens to repeat for an unrelated reason.
  const lastIndex = last.index ?? -1;
  if (lastIndex < 0) return spaced;
  const tail = spaced.slice(lastIndex).trim();
  const expectedTail = `${last[1]} ${last[2]}`;
  if (tail !== expectedTail) return spaced;

  return spaced.slice(0, lastIndex).trim();
}

/**
 * Word-level abbreviation expansions for the amphetamine/dextroamphetamine
 * combination family (Adderall / Adderall XR generics — "amphetamine-
 * dextroamphetamine mixed salts"). Field report: e-script named the drug
 * "AMPHETAMINE-DEXTROAMPHET" (openFDA-style, dextro- prefix fused onto
 * the "amphet" abbreviation of amphetamine), PioneerRx entry named it
 * "Dextroamp-Amphet" (a more aggressively truncated abbreviation of
 * dextroamphetamine, plus the same "amphet" abbreviation) — same
 * ingredients, different truncations, flagged as unknown_drug. Keys are
 * matched as WHOLE tokens only (space- or hyphen-delimited), never a
 * substring rewrite inside an unrelated longer word — see
 * foldAmphetamineFamily below, which always splits on hyphens before
 * looking a token up here.
 */
const AMPHETAMINE_ABBREV_MAP: Record<string, string> = {
  dextroamp: 'dextroamphetamine',
  dextroamphet: 'dextroamphetamine',
  amphet: 'amphetamine',
  // Round 6, fix 5 (additive): "amphetam" is another truncation of
  // amphetamine actually seen on a live PioneerRx entry
  // ("Dextroamp-Amphetam"); "dextroamphetam" added defensively for the
  // symmetric dextroamphetamine truncation, same length-truncation
  // pattern as the existing dextroamp/dextroamphet keys above.
  amphetam: 'amphetamine',
  dextroamphetam: 'dextroamphetamine'
};

const AMPHETAMINE_FAMILY_INGREDIENTS = new Set(['amphetamine', 'dextroamphetamine']);

/**
 * Amphetamine-family-only normalization pass:
 *  1. Expand the whole-token abbreviations above (splitting hyphenated
 *     compounds first, so "dextroamp-amphet" expands each side
 *     independently).
 *  2. Combo-ingredient order normalization, SCOPED to this family only
 *     (not applied generically — no other hyphenated multi-ingredient
 *     name has test coverage to prove it's safe fleet-wide): when a
 *     hyphenated token is exactly the two amphetamine-family ingredients
 *     in either order, sort them alphabetically, mirroring
 *     scripts/build-drug-data.ts's existing ingredient-alphabetization
 *     convention (there: semicolon-joined and localeCompare-sorted; here:
 *     hyphen-joined, same sort).
 *  3. Salt-phrase folding, scoped to this family only (gated on the
 *     string actually containing a family ingredient): "salts"/"salt"/
 *     "mixed" tokens carry no product-distinguishing meaning for this
 *     combination product and are dropped. Never touches "sulfate"/
 *     "sulphate" — Amphetamine SULFATE is a different, non-combo
 *     product and must stay distinct.
 *
 * Strength/dose-form equality is still enforced entirely by the
 * existing checks elsewhere in compareDrugs (extractStatedStrength
 * contradiction check, concept ingredient/strength/form comparison) —
 * this function only touches how the ingredient NAME portion folds, so
 * e.g. generic Mydayis (12.5/25/37.5/50mg) still only matches Adderall
 * XR (5/10/15/20/25/30mg) at the genuine 25mg strength overlap, and only
 * when the raw names carry no other distinguishing token — no
 * additional product-level logic is added here on purpose.
 */
function foldAmphetamineFamily(spaced: string): string {
  const words = spaced.split(' ').map((word) => {
    if (!word.includes('-')) {
      return AMPHETAMINE_ABBREV_MAP[word] ?? word;
    }
    const parts = word.split('-').map((p) => AMPHETAMINE_ABBREV_MAP[p] ?? p);
    if (parts.length === 2 && parts.every((p) => AMPHETAMINE_FAMILY_INGREDIENTS.has(p))) {
      parts.sort((a, b) => a.localeCompare(b));
    }
    return parts.join('-');
  });

  const hasFamilyIngredient = words.some(
    (w) => AMPHETAMINE_FAMILY_INGREDIENTS.has(w) || w.split('-').some((p) => AMPHETAMINE_FAMILY_INGREDIENTS.has(p))
  );
  if (!hasFamilyIngredient) return words.join(' ');

  return words.filter((w) => w !== 'salts' && w !== 'salt' && w !== 'mixed').join(' ');
}

/**
 * Normalize a free-text drug name/description for IDENTITY comparison:
 * case/punctuation/whitespace, release-qualifier phrases (see
 * RELEASE_PHRASE_FOLDS — "extended release"/"extended-release" -> er,
 * sustained release -> sr, controlled release -> cr, delayed release ->
 * dr, immediate release -> ir), common dosage-FORM abbreviations (see
 * DOSAGE_FORM_WORDS — TAB/TABS -> tablet, CAP/CAPS -> capsule, SOL ->
 * solution, SUSP -> suspension, OINT/UNG -> ointment, CRM -> cream,
 * SUPP -> suppository, INJ -> injection, GTT -> drops),
 * and number/unit spacing only — no pharmaceutical-
 * equivalence reasoning (that would need real RxNorm data — see this
 * file's header). Deliberately conservative: this can only ever fail to
 * recognize a real match (e.g. "Phosp" vs "Phosphate" spelled out
 * differently) and fall through to the concept-resolution path below,
 * never produce a false green from two actually-different drugs looking
 * superficially similar.
 */
export function normalizeDrugNameString(raw: string): string {
  let folded = raw
    .toLowerCase()
    .replace(/[®™©]/g, '')
    .replace(/[.,]/g, '')
    .replace(/\s+/g, ' ')
    .trim();

  for (const [pattern, abbrev] of RELEASE_PHRASE_FOLDS) {
    folded = folded.replace(pattern, abbrev);
  }

  // Fold away a stated release-DURATION phrase ("24 hour"/"12 hour") —
  // see foldDurationHours's doc: safe here only because compareDrugs
  // separately blocks the identity fast path on a genuine duration
  // CONTRADICTION via extractStatedDurationHours.
  folded = foldDurationHours(folded);
  // Collapse a release qualifier stated twice (once abbreviated, once
  // spelled out and re-folded above) down to one occurrence.
  folded = dedupeReleaseAbbrevs(folded);

  // Force exactly one space between a number and a trailing strength
  // unit, so "2mg" and "2 mg" fold to the same text (unit CASING is
  // already handled by the toLowerCase() above).
  const spaced = folded.replace(/(\d)(mg|mcg|ml|g|units?)\b/g, '$1 $2');

  // Round 6, fix 3 (additive): strip a trailing strength restatement that
  // exactly duplicates the strength already stated earlier in the name
  // — see foldTrailingDuplicateStrength's doc.
  const dedupedStrength = foldTrailingDuplicateStrength(spaced);

  const amphetFolded = foldAmphetamineFamily(dedupedStrength);

  return amphetFolded
    .split(' ')
    .map((tok) => DOSAGE_FORM_WORDS[tok] ?? tok)
    .join(' ');
}

/**
 * Round 6, fix 4: route-qualifier words that sometimes appear inside the
 * FORM phrase of a drug name ("...ORAL TABLET" vs "...TABLET"). Field
 * report: e-script text carries a route qualifier PioneerRx's shorter
 * free-text entry drops entirely — same drug, one side is just more
 * verbose. Only the common, unambiguous route words are listed; scoped
 * exactly the way DOSAGE_FORM_WORDS/RELEASE_PHRASE_FOLDS above document
 * their own scoping (a fixed, deliberately short list, not a general
 * fuzzy pass).
 */
const FORM_ROUTE_QUALIFIERS = new Set([
  'oral', 'subcutaneous', 'topical', 'sublingual', 'rectal', 'vaginal',
  'intramuscular', 'intravenous', 'transdermal', 'ophthalmic', 'otic',
  'nasal', 'inhalation'
]);

function extractFormRouteQualifier(normalizedName: string): string | null {
  for (const tok of normalizedName.split(' ')) {
    if (FORM_ROUTE_QUALIFIERS.has(tok)) return tok;
  }
  return null;
}

function stripFormRouteQualifiers(normalizedName: string): string {
  return normalizedName
    .split(' ')
    .filter((tok) => !FORM_ROUTE_QUALIFIERS.has(tok))
    .join(' ');
}

/**
 * Component-wise name fallback (see compareNameComponents/
 * decomposeDrugNameComponents below, and compareDrugs' unknown_drug
 * branch): field report -- source e-script "TRAMADOL 50 MG ORAL TABLET"
 * vs entered "Tramadol Hcl 50 Mg Tablet" went yellow unknown_drug because
 * the synthetic 20-concept fixture (and, live, the local openFDA-derived
 * dataset when it simply doesn't carry a drug) resolved NEITHER side to a
 * concept, so no name-based comparison ever ran at all -- despite the two
 * names describing the same drug (one states the salt, the other
 * doesn't). RxNorm-grade resolution for every drug is separate, longer-
 * term work (see this file's header); this is a conservative, purely
 * name-based structural comparison that only ever runs as a LAST RESORT
 * when concept resolution has already failed for at least one side.
 *
 * Curated salt/ester tokens this fallback recognizes. NOT exhaustive --
 * covers the common salts/esters seen in this engine's field reports plus
 * standard pharmacy nomenclature. A salt token NOT on this list is simply
 * treated as an ordinary ingredient token instead, which can only make
 * the ingredient-set comparison below MORE strict (never a false green)
 * -- safe by this file's IRON RULE regardless of gaps in this list.
 */
export const SALT_TOKENS = new Set([
  'hcl', 'hydrochloride', 'hbr', 'hydrobromide', 'sodium', 'potassium',
  'calcium', 'magnesium', 'sulfate', 'tartrate', 'bitartrate', 'succinate',
  'maleate', 'mesylate', 'besylate', 'tosylate', 'citrate', 'fumarate',
  'pamoate', 'phosphate', 'acetate', 'valerate', 'propionate',
  'dipropionate', 'monohydrate', 'dihydrate'
]);

/**
 * Field report (2026-08-13, RXVERIFY-TROUBLESHOOT): source "METOPROLOL
 * SUCCINATE (XL) 50 MG ORAL TABLET" vs entered "Metoprolol Succ Er 50 Mg
 * Tab" went yellow unknown_drug -- entered's "Succ" isn't in SALT_TOKENS
 * (only the spelled-out "succinate" is), so it fell into the generic
 * ingredient-token bucket below and broke rule 1's ingredient-set
 * equality against source's {"metoprolol"}. "Succ"/"Tart" are the two
 * salt abbreviations PioneerRx's free-text entry actually produces for
 * this engine's field reports and are unambiguous (no other recognized
 * salt/ingredient/form/route word starts the same way) -- scoped to
 * exactly these two per the owner's report rather than guessing at a
 * longer list. A token not in this map is used as-is (falls through to
 * the ordinary SALT_TOKENS check unchanged).
 */
const SALT_ABBREVIATIONS: Record<string, string> = {
  succ: 'succinate',
  tart: 'tartrate'
};

/**
 * Release-RATE equivalence class for the component-wise name fallback
 * ONLY (decomposeDrugNameComponents/compareNameComponents below) --
 * field report (2026-08-13): Metoprolol Succinate XL and Metoprolol Succ
 * ER are the SAME dispensed product (owner confirmed; XL is simply
 * PioneerRx/e-script shorthand for the same once-daily extended-release
 * succinate formulation), so this fallback treats XL/ER/XR/SR/LA/CR as
 * one interchangeable class rather than blocking green on rule 6 --
 * BUT ONLY when ingredientTokens contains "metoprolol" (see
 * decomposeDrugNameComponents' gating on this map, applied AFTER the
 * token loop once ingredientTokens is fully known).
 *
 * Reviewer round 2, BLOCKER 1 (reviewer-reproduced live false green):
 * this map used to apply UNCONDITIONALLY to every drug name, and the
 * reviewer reproduced compareDrugs("Bupropion Xl 150 Mg Tablet",
 * "Bupropion Sr 150 Mg Tablet") going GREEN through it -- Bupropion
 * SR/XL is the exact previously-fixed live false-green
 * RELEASE_QUALIFIER_TOKENS' own doc (below) exists to prevent (they are
 * genuinely different, non-interchangeable products that merely share
 * an openFDA dosage_form). Metoprolol succinate is the ONE
 * owner-confirmed interchangeable case; this map must never be applied
 * more broadly than that single ingredient without a matching owner
 * report for whatever other ingredient is being added. Deliberately NOT
 * a change to RELEASE_QUALIFIER_TOKENS/RELEASE_ABBREVS or
 * resolveConceptByName's own release-qualifier narrowing above -- this
 * equivalence class only ever applies inside this fallback, which
 * itself only ever runs as a last resort once concept resolution has
 * already failed on both names.
 *
 * DR (delayed-release) and IR (immediate-release) are intentionally left
 * OUT of the class -- they describe a genuinely different release
 * profile (delayed onset / no modification at all) than the once/twice-
 * daily extended-release family XL/ER/XR/SR/LA/CR all describe, so a
 * name stating DR or IR still only matches another name stating the
 * exact same bare qualifier (or none at all), even for metoprolol.
 */
const RELEASE_EQUIVALENCE_CLASS: Record<string, string> = {
  xl: 'extended-release',
  er: 'extended-release',
  xr: 'extended-release',
  sr: 'extended-release',
  la: 'extended-release',
  cr: 'extended-release'
};

/**
 * Release-qualifier tokens this fallback recognizes and strips out of the
 * ingredient-token bucket -- a SUPERSET of RELEASE_QUALIFIER_TOKENS (adds
 * "xr"/"la", which the earlier pre-concept-resolution pathway never
 * needed to recognize) since this fallback computes its own `release`
 * value locally (see decomposeDrugNameComponents) rather than reusing
 * extractReleaseQualifier's, so it can fold XL/ER/XR/SR/LA/CR down to one
 * equivalence class -- see RELEASE_EQUIVALENCE_CLASS' doc.
 */
const COMPONENT_RELEASE_TOKENS = new Set(['er', 'sr', 'cr', 'dr', 'ir', 'xl', 'xr', 'la']);

/**
 * Curated route-of-administration tokens for the component-wise
 * name-fallback comparison only. Deliberately a SEPARATE, smaller list
 * from FORM_ROUTE_QUALIFIERS above (that one feeds the pre-concept-
 * resolution identity fast path and is independently scoped) -- this one
 * additionally recognizes the common abbreviations "po"/"sl" that show up
 * in free-text drug names.
 *
 * Reviewer round 1, should-fix 5: this list deliberately does NOT include
 * FORM_ROUTE_QUALIFIERS' subcutaneous/intramuscular/intravenous/inhalation
 * -- those are left to fall through to the ordinary ingredient-token
 * bucket below (never recognized as a tolerated one-sided route by this
 * fallback), so a name stating one of these higher-stakes parenteral/
 * inhalation routes must match EXACTLY on both sides (or be absent from
 * both) to ever reach a green here, the same conservative treatment
 * "injectable"/"injection" get below -- never silently tolerated
 * one-sided.
 *
 * Reviewer round 1, should-fix 4: "injection"/"injectable" are listed
 * here AS ROUTES on purpose, even though "injection" is ALSO a canonical
 * dosage-FORM word (DOSAGE_FORM_WORDS folds "inj" -> "injection", which
 * feeds COMPONENT_FORM_TOKENS below). Route tokens are bucketed BEFORE
 * form tokens in decomposeDrugNameComponents' loop, so a raw "injection"/
 * "injectable" token is always consumed as a ROUTE, never as the FORM --
 * meaning `form` can never be populated from an injectable name, and rule
 * 3 below (form must be stated and agree on both sides) then ALWAYS
 * blocks green for any injectable-route pair. This is intentional and
 * fail-safe (injectable products stay conservatively excluded from this
 * newer, less-reviewed fallback rather than risking a green on
 * unreviewed dose-form reasoning), not an oversight -- see should-fix 4.
 */
const COMPONENT_ROUTE_TOKENS = new Set([
  'oral', 'po', 'sublingual', 'sl', 'topical', 'ophthalmic', 'otic',
  'nasal', 'rectal', 'vaginal', 'transdermal', 'injectable', 'injection'
]);

/**
 * Canonical dosage-form tokens the component-wise name-fallback
 * comparison recognizes. Reuses DOSAGE_FORM_WORDS' own canonical
 * (folded-TO) values -- so any raw abbreviation normalizeDrugNameString
 * already knows how to fold ("tab" -> "tablet", etc) is recognized here
 * too, by the time this runs -- plus a handful of additional common forms
 * that already appear spelled out and need no folding.
 */
const COMPONENT_FORM_TOKENS = new Set<string>([
  ...Object.values(DOSAGE_FORM_WORDS),
  'inhaler', 'lotion', 'gel', 'spray', 'patch', 'elixir', 'syrup', 'powder', 'lozenge', 'foam', 'shampoo'
]);

function tokenSetsEqual(a: Set<string>, b: Set<string>): boolean {
  if (a.size !== b.size) return false;
  for (const v of a) {
    if (!b.has(v)) return false;
  }
  return true;
}

interface DrugNameComponents {
  ingredientTokens: Set<string>;
  strength: string | null;
  form: string | null;
  salts: Set<string>;
  routes: Set<string>;
  release: string | null;
  /**
   * Ratio-shaped numeric tokens ("5/325", "5-325", decimals included) IN
   * ENCOUNTER ORDER -- see decomposeDrugNameComponents' isRatioToken doc
   * for why these get their own bucket instead of either being disposed
   * of as strength "noise" or folded into the generic ingredient set.
   */
  ratios: string[];
}

/**
 * Decompose a free-text drug name into structured components for
 * compareNameComponents below. Reuses this file's existing normalization/
 * extraction helpers -- normalizeDrugNameString (case/whitespace/
 * punctuation, dosage-form folding, release-phrase folding already
 * applied), extractStatedStrength/extractStatedConcentrationStrength, and
 * extractReleaseQualifier -- and adds only the NEW salt/route vocabulary
 * (SALT_TOKENS/COMPONENT_ROUTE_TOKENS) plus the bucketing logic that pulls
 * them (and the form word, and release-qualifier tokens) out of the
 * ingredient tokens. Whatever's left is the ingredient token set
 * (order-insensitive).
 */
function decomposeDrugNameComponents(rawName: string): DrugNameComponents {
  const concStrength = extractStatedConcentrationStrength(rawName);
  const strength = concStrength ?? extractStatedStrength(rawName);

  const normalized = normalizeDrugNameString(rawName);
  // Strip stray parentheses off each token BEFORE classification -- field
  // report (2026-08-13): source names carry a parenthesized release
  // qualifier, e.g. "METOPROLOL SUCCINATE (XL) 50 MG ORAL TABLET".
  // normalizeDrugNameString doesn't strip parens (other callers rely on
  // them surviving), so without this the token stayed "(xl)", matched
  // nothing in COMPONENT_RELEASE_TOKENS, and fell into the generic
  // ingredient bucket as a literal "(xl)" token -- breaking rule 1's
  // ingredient-set equality against a source/entered pair that's
  // otherwise identical. Splitting on whitespace first (so an
  // independent "(90.0000"-style token isn't affected elsewhere) then
  // stripping parens per-token is safe here: this bucketing is local to
  // this fallback only.
  const tokens = normalized
    .split(' ')
    .filter(Boolean)
    .map((tok) => tok.replace(/[()]/g, ''))
    .filter(Boolean);

  const isNumeric = (tok: string): boolean => /^\d+(\.\d+)?%?$/.test(tok);
  const isUnitWord = (tok: string): boolean => /^(mcg|mg|ml|g|units?|unit)$/.test(tok);
  // Reviewer round 1, BLOCKER 1 fix (a): a token is only a disposable
  // "unit-glued-across-a-slash" remnant (e.g. "mg/0.5", produced by this
  // file's own digit+unit spacing step acting on "12.5 mg/0.5 mL" --
  // see extractStatedConcentrationStrength's doc) when AT LEAST ONE half
  // is a literal unit WORD. A token whose halves are BOTH pure numbers
  // ("5/325") is a combo-product dose RATIO, not unit noise, and must
  // never be silently dropped here -- see isRatioToken below, which is
  // exactly the case this used to wrongly swallow (a confirmed live
  // false GREEN: "Hydrocodone/Acetaminophen 5/325" vs ".../10/325").
  const isSlashUnit = (tok: string): boolean =>
    tok.includes('/') &&
    tok.split('/').every((p) => isNumeric(p) || isUnitWord(p)) &&
    tok.split('/').some((p) => isUnitWord(p));
  // Reviewer round 1, BLOCKER 1 fix (b): a token that is PURELY a numeric
  // ratio -- "5/325" or "5-325", decimals allowed on either side -- is
  // dose-identity-bearing (e.g. hydrocodone/acetaminophen's two combo
  // strengths) and must never be treated as disposable strength noise
  // OR folded anonymously into the generic ingredient-token set (a
  // hyphen-ratio happens to survive there today only as an accident of
  // tokenization, not by design). Captured into its own `ratios` list,
  // IN ORDER, and compared for exact equality by compareNameComponents
  // below -- any mismatch (including a length mismatch) blocks green.
  const isRatioToken = (tok: string): boolean => /^\d+(?:\.\d+)?[/-]\d+(?:\.\d+)?$/.test(tok);

  const salts = new Set<string>();
  const routes = new Set<string>();
  let form: string | null = null;
  // Reviewer round 2, BLOCKER 1 fix: kept as the RAW bare release token
  // (never equivalence-mapped) until AFTER the loop, when ingredientTokens
  // is fully known -- see the equivalence-gating below `release`'s
  // definition for why this can't be decided token-by-token during the
  // loop itself.
  let rawRelease: string | null = null;
  const ingredientTokens = new Set<string>();
  const ratios: string[] = [];

  for (const tok of tokens) {
    if (COMPONENT_RELEASE_TOKENS.has(tok)) {
      // First release token wins, same "keep only one" rule as `form`
      // below.
      if (rawRelease === null) rawRelease = tok;
      continue;
    }
    if (isNumeric(tok) || isUnitWord(tok) || isSlashUnit(tok)) continue;
    if (isRatioToken(tok)) {
      ratios.push(tok);
      continue;
    }
    // Salt-abbreviation normalization (SALT_ABBREVIATIONS) applied before
    // the SALT_TOKENS lookup, so "succ"/"tart" resolve to the same
    // canonical salt as the spelled-out "succinate"/"tartrate" would --
    // see SALT_ABBREVIATIONS' doc.
    const saltCandidate = SALT_ABBREVIATIONS[tok] ?? tok;
    if (SALT_TOKENS.has(saltCandidate)) {
      salts.add(saltCandidate);
      continue;
    }
    if (COMPONENT_ROUTE_TOKENS.has(tok)) {
      routes.add(tok);
      continue;
    }
    if (COMPONENT_FORM_TOKENS.has(tok)) {
      if (form === null) form = tok;
      continue;
    }
    ingredientTokens.add(tok);
  }

  // Reviewer round 2, BLOCKER 1 (reviewer-reproduced live false green):
  // RELEASE_EQUIVALENCE_CLASS must NOT apply universally -- reviewer
  // reproduced compareDrugs("Bupropion Xl 150 Mg Tablet",
  // "Bupropion Sr 150 Mg Tablet") going GREEN through it, even though
  // Bupropion SR/XL is the EXACT previously-fixed live false-green
  // RELEASE_QUALIFIER_TOKENS' own doc (above) exists to prevent — they
  // are non-interchangeable, clinically distinct products. Metoprolol
  // succinate is the ONE owner-confirmed case where XL/ER/XR/SR/LA/CR
  // really are interchangeable (2026-08-13 field report), so the
  // equivalence class is gated on ingredientTokens containing
  // "metoprolol" specifically -- everywhere else, release tokens stay
  // identity-distinct (their own raw bare value), exactly as before this
  // fallback ever folded them. Decided HERE, after the loop, rather than
  // inline while classifying `rawRelease` above: ingredientTokens isn't
  // fully known until every token has been seen, and a release token can
  // appear before the ingredient word in some free-text order.
  //
  // Rule 1 (ingredient-set equality) always runs before rule 6 checks
  // this `release` value, so by the time two sides' `release` are ever
  // compared, their ingredientTokens are already known to be IDENTICAL —
  // one side having "metoprolol" therefore guarantees the other does
  // too, and gating each side independently on its own ingredientTokens
  // is safe.
  const release =
    rawRelease !== null && ingredientTokens.has('metoprolol')
      ? (RELEASE_EQUIVALENCE_CLASS[rawRelease] ?? rawRelease)
      : rawRelease;

  return { ingredientTokens, strength, form, salts, routes, release, ratios };
}

/**
 * Conservative, order-sensitive structured comparison of two raw drug
 * names -- see this section's header comment for why this exists and
 * compareDrugs' unknown_drug branch for the (single) call site. Returns a
 * match, or a non-match with an optional human-readable `note` (populated
 * for a genuine salt/route conflict, so the resulting yellow explanation
 * can name the two differing values -- e.g. "metoprolol tartrate" vs
 * "metoprolol succinate" must never be blurred into the same verdict).
 *
 * IRON RULE: this can only ever confirm a GREEN or fall through to the
 * existing unknown_drug YELLOW -- name-derived evidence alone must never
 * produce a RED (see this file's header). Order matters, most specific
 * disqualifier first:
 *  0. (reviewer round 1, should-fix 3) defense-in-depth: an EMPTY
 *     ingredient-token set on either side is never a match, even against
 *     another empty set -- without this, a name consisting entirely of
 *     salt/route/form/strength words (no leftover ingredient token at
 *     all) could otherwise vacuously satisfy rule 1's set-equality check
 *     via two empty sets. Guards against future vocabulary growth
 *     (SALT_TOKENS/COMPONENT_ROUTE_TOKENS/COMPONENT_FORM_TOKENS all
 *     absorbing more words over time) quietly reopening this;
 *  1. ingredient token sets must be equal (order-insensitive);
 *  1b. (reviewer round 1, BLOCKER 1 fix) ratio-shaped numeric tokens
 *      ("5/325", "5-325") must be equal IN ORDER -- a combo product's
 *      dose ratio is identity-bearing, never disposable strength noise;
 *  2. both sides must state a strength, and it must agree;
 *  3. dosage form must be stated and agree on both sides;
 *  4. salt: one side stating a salt the other is silent on is tolerated;
 *     BOTH stating a salt that DIFFERS is a genuine, clinically real
 *     distinction and is never a match;
 *  5. route: same asymmetric rule as salt;
 *  6. release qualifier: any difference (asymmetric or a genuine
 *     conflict) defers to the existing behavior -- never greened here.
 */
function compareNameComponents(
  srcName: string,
  entName: string
): { match: true } | { match: false; note: string | null } {
  const src = decomposeDrugNameComponents(srcName);
  const ent = decomposeDrugNameComponents(entName);

  if (src.ingredientTokens.size === 0 || ent.ingredientTokens.size === 0) {
    return { match: false, note: null };
  }

  if (!tokenSetsEqual(src.ingredientTokens, ent.ingredientTokens)) {
    return { match: false, note: null };
  }

  if (src.ratios.length !== ent.ratios.length || src.ratios.some((r, i) => r !== ent.ratios[i])) {
    return { match: false, note: null };
  }

  if (!src.strength || !ent.strength || src.strength !== ent.strength) {
    return { match: false, note: null };
  }

  if (!src.form || !ent.form || src.form !== ent.form) {
    return { match: false, note: null };
  }

  if (src.salts.size > 0 && ent.salts.size > 0 && !tokenSetsEqual(src.salts, ent.salts)) {
    return {
      match: false,
      note: `stated salt differs (${[...src.salts].sort().join('/')} vs ${[...ent.salts].sort().join('/')})`
    };
  }

  if (src.routes.size > 0 && ent.routes.size > 0 && !tokenSetsEqual(src.routes, ent.routes)) {
    return {
      match: false,
      note: `stated route differs (${[...src.routes].sort().join('/')} vs ${[...ent.routes].sort().join('/')})`
    };
  }

  if (src.release !== ent.release) {
    return { match: false, note: null };
  }

  return { match: true };
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

  // DRUG IDENTITY BY NAME, FIRST: a real e-script's NDC almost never
  // equals the NDC actually dispensed (many NDCs exist per drug —
  // different labeler/package/lot), and in the live overlay the entered
  // side never carries an NDC at all (PioneerRx's item field only
  // exposes a typed name — see overlay/.../Uia/FieldReader.cs). Requiring
  // NDC agreement as the primary signal means a real, correctly-matching
  // drug would routinely fail to show green. So: if BOTH sides state a
  // drug name, normalize (case/punctuation/whitespace only — no
  // pharmaceutical-equivalence reasoning) and treat an exact normalized
  // match as the drug identity match, regardless of whether either side's
  // NDC is present or whether the NDCs agree. NDC stays available below
  // for behind-the-scenes lookup only (the local openFDA dataset, used to
  // resolve ingredient/strength/form for the RED/YELLOW paths) — a
  // precise RxNorm-rxcui identity compare is documented future work (see
  // this file's header) once Will's UTS license lands.
  // Release-DURATION contradiction guard: normalizeDrugNameString folds
  // away a stated "N hour" duration phrase entirely (see
  // foldDurationHours) so that a name stating it on only one side still
  // matches. That fold alone can't tell "one side silent" apart from
  // "both sides state a DIFFERENT duration" — so check the raw strings
  // here and refuse the fast-path green on a genuine contradiction (a
  // 12-hour and a 24-hour product are not the same dispense, even if
  // everything else about the name is identical).
  const srcDurationHours = src.name ? extractStatedDurationHours(src.name) : null;
  const entDurationHours = ent.name ? extractStatedDurationHours(ent.name) : null;
  const durationConflict =
    srcDurationHours !== null && entDurationHours !== null && srcDurationHours !== entDurationHours;

  const srcNameNorm = src.name ? normalizeDrugNameString(src.name) : null;
  const entNameNorm = ent.name ? normalizeDrugNameString(ent.name) : null;
  if (srcNameNorm && entNameNorm && srcNameNorm === entNameNorm && !durationConflict) {
    return {
      status: 'green',
      reasonCode: 'name_identity_match',
      explanation: `Drug name/description matches exactly after normalization ("${src.name}" / "${ent.name}") — NDC not compared directly, since the dispensed NDC routinely differs from the e-script's stated NDC.`
    };
  }

  // Round 6, fix 4: a route qualifier stated in the FORM phrase on only
  // ONE side ("...Oral Tablet" vs "...Tablet") is not itself evidence of
  // a different product — fold it out and retry the identity comparison.
  // When BOTH sides state a route qualifier and it genuinely DIFFERS
  // ("oral tablet" vs "sublingual tablet"), that IS a real difference —
  // this check refuses to fold in that case, so a real route mismatch
  // still falls through to the ordinary (non-green) comparison below.
  if (srcNameNorm && entNameNorm && !durationConflict) {
    const srcRoute = extractFormRouteQualifier(srcNameNorm);
    const entRoute = extractFormRouteQualifier(entNameNorm);
    const routesConflict = srcRoute !== null && entRoute !== null && srcRoute !== entRoute;
    if (!routesConflict && (srcRoute !== null || entRoute !== null)) {
      const srcNoRoute = stripFormRouteQualifiers(srcNameNorm);
      const entNoRoute = stripFormRouteQualifiers(entNameNorm);
      if (srcNoRoute === entNoRoute) {
        return {
          status: 'green',
          reasonCode: 'name_identity_match',
          explanation: `Drug name/description matches after folding out a route qualifier stated on only one side ("${src.name}" / "${ent.name}").`
        };
      }
    }
  }

  const srcNdc = src.ndc ? parseNdc(src.ndc) : null;
  const entNdc = ent.ndc ? parseNdc(ent.ndc) : null;

  if (srcNdc && entNdc && srcNdc.normalized11 === entNdc.normalized11) {
    return {
      status: 'green',
      reasonCode: 'exact_match',
      explanation: 'NDC matches exactly.'
    };
  }

  // Cross-check strengths STATED in the raw name strings. A free-text
  // fallback concept lookup keys on the brand/ingredient word, so
  // "Lisinopril 20mg" and "Lisinopril 10mg" can resolve to the same
  // fixture concept — the stated strengths must not be allowed to
  // contradict silently.
  //
  // Round 6, fix 2: a compound CONCENTRATION strength ("12.5 mg/0.5 mL")
  // is ONE unit, not two independently-competing single strengths — see
  // extractStatedConcentrationStrength's doc. Try that first whenever
  // either side's name looks like it's stating one (hasConcentrationSignal
  // — a literal "/" or the specific OCR-glued shape); only fall back to
  // the plain single-value check below when NEITHER side shows any such
  // signal at all. Per the safety rule: if one side signals a
  // concentration strength but it can't be cleanly parsed even after the
  // scoped OCR-confusable fixes, this NEVER falls back to the naive
  // single-value regex for that side (which is exactly what produced the
  // original false RED — a fragment like "5ml" grabbed from a garbled
  // "...IO.5 ML..." remainder) — it just skips the strength-conflict
  // check entirely and lets the comparison continue (typically landing on
  // a yellow verdict further down), never guessing a RED.
  const srcConcStrength = src.name ? extractStatedConcentrationStrength(src.name) : null;
  const entConcStrength = ent.name ? extractStatedConcentrationStrength(ent.name) : null;
  const srcHasConcSignal = src.name ? hasConcentrationSignal(src.name) : false;
  const entHasConcSignal = ent.name ? hasConcentrationSignal(ent.name) : false;

  let srcStatedStrength: string | null = null;
  let entStatedStrength: string | null = null;

  if (srcConcStrength && entConcStrength) {
    if (srcConcStrength !== entConcStrength) {
      return {
        status: 'red',
        reasonCode: 'drug_mismatch',
        explanation: `Drug does not match: concentration strength stated in the drug names differs (${srcConcStrength} vs ${entConcStrength}).`
      };
    }
    // Compound strengths agree — do NOT also run the single-value
    // cross-check below; it would risk comparing two DIFFERENT halves of
    // the same compound value (e.g. one side's per-dose mg vs the other's
    // per-volume mL) as if they were competing single strengths.
  } else if (!srcHasConcSignal && !entHasConcSignal) {
    // Neither side shows any concentration-strength notation — safe to
    // run the plain single-strength cross-check exactly as before.
    srcStatedStrength = src.name ? extractStatedStrength(src.name) : null;
    entStatedStrength = ent.name ? extractStatedStrength(ent.name) : null;
    if (srcStatedStrength && entStatedStrength && srcStatedStrength !== entStatedStrength) {
      return {
        status: 'red',
        reasonCode: 'drug_mismatch',
        explanation: `Drug does not match: strength stated in the drug names differs (${srcStatedStrength} vs ${entStatedStrength}).`
      };
    }
  }
  // else: at least one side shows concentration notation but a confident
  // compound match couldn't be confirmed on both sides — per the safety
  // rule above, strength comparison for this pair is indeterminate; fall
  // through without emitting a RED (the concept-resolution path below
  // will typically land on a yellow verdict).

  const srcConceptViaNdc = srcNdc ? provider.getConcept(srcNdc.normalized11) : null;
  const entConceptViaNdc = entNdc ? provider.getConcept(entNdc.normalized11) : null;
  const srcConcept = srcConceptViaNdc ?? (src.name ? provider.getConcept(src.name) : null);
  const entConcept = entConceptViaNdc ?? (ent.name ? provider.getConcept(ent.name) : null);

  if (!srcConcept || !entConcept) {
    // Component-wise name fallback (see compareNameComponents' doc above):
    // concept resolution failed for at least one side -- before giving up,
    // try a STRUCTURED comparison of the two raw names themselves. Guarded
    // by !durationConflict for the same reason the name-identity and
    // route-fold fast paths above are: a genuine stated-duration
    // contradiction (12 hour vs 24 hour) must never be resolved to green
    // by any name-based path, no matter how the rest of the name
    // normalizes.
    if (!durationConflict && src.name && ent.name) {
      const componentResult = compareNameComponents(src.name, ent.name);
      if (componentResult.match) {
        return {
          status: 'green',
          reasonCode: 'name_component_match',
          explanation: `Names match after component normalization (salt/route wording differs but is compatible); not resolved against a drug database ("${src.name}" / "${ent.name}").`
        };
      }
      if (componentResult.note) {
        return {
          status: 'yellow',
          reasonCode: 'unknown_drug',
          explanation: `Could not resolve one or both drugs to a known concept; needs human review (${componentResult.note}).`
        };
      }
    }
    return {
      status: 'yellow',
      reasonCode: 'unknown_drug',
      explanation: 'Could not resolve one or both drugs to a known concept; needs human review.'
    };
  }

  // Did AT LEAST ONE side's concept come from the NEW, approximate
  // NAME-based resolution (LocalNdcProvider's openFDA nameIndex — see
  // resolveConceptByName in this file — or FixtureProvider's pre-existing
  // name lookup) rather than an NDC? NDC pins an exact product; a name
  // match is a heuristic. Both the green upgrade below and the red-guard
  // further down key off this.
  const srcConceptViaName = srcConceptViaNdc === null && srcConcept !== null;
  const entConceptViaName = entConceptViaNdc === null && entConcept !== null;
  const nameResolutionUsed = srcConceptViaName || entConceptViaName;

  // Confirmed live false GREEN, fixed here: openFDA's dosage_form does
  // NOT distinguish release-RATE variants (SR vs XL both normalize to
  // the same "tablet, extended release"-style text), so two name-
  // resolved concepts can share an identical derived
  // ingredient/strength/doseForm key while being CLINICALLY DIFFERENT,
  // non-interchangeable products — e.g. "Bupropion Hydrochloride SR
  // 150 MG" and "Bupropion Hydrochloride XL 150 MG" each resolve
  // cleanly (resolveConceptByName confirms each against its OWN stated
  // qualifier) but must never be treated as the same concept. Detect a
  // GENUINE qualifier contradiction directly from the two RAW query
  // strings (same extractReleaseQualifier used inside
  // resolveConceptByName) and use it below to block the concept_match
  // green even though the derived fields otherwise agree.
  const srcQualifier = src.name ? extractReleaseQualifier(src.name) : null;
  const entQualifier = ent.name ? extractReleaseQualifier(ent.name) : null;
  // A side's release-rate qualifier is UNCONFIRMED when its concept came
  // from NAME resolution (not NDC — an NDC pins the exact product
  // regardless of whether a release qualifier is spelled out in text)
  // AND its raw text states no qualifier at all. Mirrors the existing
  // strengthUnverified precedent immediately below: asymmetric
  // confirmation (one side's text states "SR", the other's name-resolved
  // side says nothing about release rate at all) must not be silently
  // treated as a match just because the resolved concepts happen to
  // share a derived key.
  const srcQualifierUnconfirmed = srcConceptViaName && srcQualifier === null;
  const entQualifierUnconfirmed = entConceptViaName && entQualifier === null;
  const qualifierConflict =
    (srcQualifier !== null && entQualifier !== null && srcQualifier !== entQualifier) ||
    (srcQualifier !== null && entQualifierUnconfirmed) ||
    (entQualifier !== null && srcQualifierUnconfirmed);

  // A side's strength is VERIFIED if the concept came from an NDC (the
  // NDC pins the exact product) or its raw name states a strength. A
  // name-resolved side with no stated strength gives us no basis to
  // claim "same strength" — that claim must not appear in a verdict.
  const srcStrengthVerified = srcConceptViaNdc !== null || srcStatedStrength !== null || srcConcStrength !== null;
  const entStrengthVerified = entConceptViaNdc !== null || entStatedStrength !== null || entConcStrength !== null;
  const strengthUnverified = !srcStrengthVerified || !entStrengthVerified;

  if (srcConcept.rxcui === entConcept.rxcui) {
    // Same concept, but NDCs differ -> package size difference only.
    if (srcNdc && entNdc && srcNdc.labeler === entNdc.labeler && srcNdc.product === entNdc.product) {
      return {
        status: 'yellow',
        reasonCode: 'pack_size',
        explanation: `Same product (${srcConcept.name}), different package size only.`
      };
    }
    if (strengthUnverified) {
      return {
        status: 'yellow',
        reasonCode: 'strength_unverified',
        explanation: `Both sides resolve to ${srcConcept.ingredient} ${srcConcept.doseForm}, but one side states no strength, so the strengths cannot be confirmed equal; needs human review.`
      };
    }
    // NEW: at least one side reached this IDENTICAL derived concept via
    // NAME resolution (not NDC), with a strength confirmed on both
    // sides -- e.g. "Vraylar 1.5 Mg Capsule" / "CARIPRAZINE 1.5 MG ORAL
    // CAPSULE" resolving to the same underlying openFDA product record.
    // That's a stronger, more specific claim than the routine
    // generic_substitution below (which is "same ingredient/strength/
    // form, different actual product" — e.g. brand pinned by NDC vs a
    // DIFFERENT manufacturer's generic resolved by name); surface it
    // distinctly rather than folding it into the same message. A pair
    // resolved entirely via NDC (nameResolutionUsed false) always falls
    // through to the unchanged generic_substitution/pack_size behavior
    // below, exactly as before this feature existed.
    if (nameResolutionUsed) {
      if (qualifierConflict) {
        // openFDA's dosage_form data artifact (see qualifierConflict's
        // doc above): the derived key matches, but the two RAW query
        // strings state DIFFERENT release-rate qualifiers (e.g. SR vs
        // XL) -- these are clinically different, non-interchangeable
        // products. Never green, and don't even corroborate via
        // generic_substitution (that message says "routine... dispensed
        // under a different NDC/brand," which would be actively
        // misleading here) -- fall to the same conservative
        // unknown_drug yellow any other unresolved-via-name pair gets.
        return {
          status: 'yellow',
          reasonCode: 'unknown_drug',
          explanation: `Could not resolve one or both drugs to a known concept; needs human review (stated release-rate qualifier differs: ${srcQualifier ?? 'none stated'} vs ${entQualifier ?? 'none stated'}).`
        };
      }
      return {
        status: 'green',
        reasonCode: 'concept_match',
        explanation: `Both resolve to ${srcConcept.ingredient} ${srcConcept.strength} ${srcConcept.doseForm} ("${srcConcept.name}" / "${entConcept.name}").`
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
    if (strengthUnverified) {
      return {
        status: 'yellow',
        reasonCode: 'strength_unverified',
        explanation: `Both sides resolve to ${srcConcept.ingredient} ${srcConcept.doseForm}, but one side states no strength, so the strengths cannot be confirmed equal; needs human review.`
      };
    }
    return {
      status: 'yellow',
      reasonCode: 'generic_substitution',
      explanation: `Same ingredient, strength, and form (${srcConcept.ingredient} ${srcConcept.strength} ${srcConcept.doseForm}), different product record — routine generic substitution.`
    };
  }

  // IRON RULE for NAME-based concept resolution: this feature's lookup
  // is heuristic (a free-text brand/generic match, not an exact NDC), so
  // it may ONLY ever CONFIRM equivalence (the green branch above) or
  // corroborate today's existing yellow — it must NEVER be the reason a
  // pair that used to fall through to `unknown_drug` yellow (because
  // LocalNdcProvider's old getConcept(name) always returned null, and
  // still does whenever it can't confidently resolve — see
  // resolveConceptByName's doc) instead turns red now that a name
  // successfully resolves to a genuinely different concept. So: when at
  // least one side's concept came from name resolution and the
  // ingredient/strength/form genuinely differ, fall back to the EXACT
  // SAME unknown_drug yellow this pair would have produced before this
  // feature existed, optionally enriched with a formsByIngredient
  // confirmation note (never changes status/reasonCode — see
  // knownFormsFor's doc). A pair resolved entirely via NDC
  // (nameResolutionUsed false) is unaffected and still reaches the
  // unchanged drug_mismatch red below, exactly as before.
  if (nameResolutionUsed) {
    let note = '';
    if (srcConcept.ingredient === entConcept.ingredient) {
      const forms = provider.knownFormsFor?.(srcConcept.ingredient) ?? null;
      if (forms && forms.length === 1) {
        note = ` (for reference: ${srcConcept.ingredient} is only known to come as ${forms[0]} in this dataset)`;
      }
    }
    return {
      status: 'yellow',
      reasonCode: 'unknown_drug',
      explanation: `Could not resolve one or both drugs to a known concept; needs human review.${note}`
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
