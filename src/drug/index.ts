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

/**
 * Loaded once per process and cached at module scope — the dataset is
 * ~130k concepts / ~250k NDCs, and every LocalNdcProvider instance
 * (e.g. one per test) should share it rather than re-parsing gzipped
 * JSON repeatedly.
 */
let cachedDataset: { concepts: LocalConcept[]; ndcIndex: Map<string, number> } | null = null;

function defaultDataPath(): string {
  const here = path.dirname(fileURLToPath(import.meta.url));
  // here = <repo>/src/drug (dev, via tsx) or <repo>/dist/drug (built) —
  // both are two directories below the repo root, so the relative path
  // back up to data/ is the same in both cases.
  return path.join(here, '..', '..', 'data', 'ndc-data.json.gz');
}

function loadDataset(dataPath: string): { concepts: LocalConcept[]; ndcIndex: Map<string, number> } {
  const gz = readFileSync(dataPath);
  const json = gunzipSync(gz).toString('utf8');
  const parsed = JSON.parse(json) as LocalDrugData;
  const ndcIndex = new Map<string, number>(Object.entries(parsed.ndcIndex));
  return { concepts: parsed.concepts, ndcIndex };
}

/**
 * REAL, LOCAL, OFFLINE drug provider backed by the bundled openFDA NDC
 * dataset (data/ndc-data.json.gz). Reads the file ONCE into an
 * in-memory Map at construction (or on first use, cached at module
 * scope) — every `getConcept` call afterward is a plain Map lookup.
 * ZERO network calls happen anywhere in this class or its callers.
 *
 * Name-based (non-NDC) lookup is intentionally NOT implemented here: a
 * real e-prescription/PioneerRx comparison almost always carries an
 * NDC, and building free-text matching against ~130k noisy openFDA
 * generic/brand strings risks a wrong match (a false green) if done
 * carelessly. Returning null for a name-only query is the conservative
 * choice — it falls through to the engine's existing `unknown_drug`
 * yellow verdict rather than guessing. Follow-on: a real name-search
 * index (tokenized, ingredient-aware) could remove this gap; flagged,
 * not implemented now.
 */
export class LocalNdcProvider implements RxNormProvider {
  private readonly concepts: LocalConcept[];
  private readonly ndcIndex: Map<string, number>;

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
  }

  getConcept(ndcOrName: string): RxConcept | null {
    const parsed = parseNdc(ndcOrName);
    if (!parsed) return null;
    const index = this.ndcIndex.get(parsed.normalized11);
    if (index === undefined) return null;
    const concept = this.concepts[index];
    if (!concept) return null;
    return {
      rxcui: deriveRxcui(concept),
      name: deriveName(concept),
      ingredient: concept.ingredient,
      strength: concept.strength,
      doseForm: concept.doseForm
    };
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
  amphet: 'amphetamine'
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

  const amphetFolded = foldAmphetamineFamily(spaced);

  return amphetFolded
    .split(' ')
    .map((tok) => DOSAGE_FORM_WORDS[tok] ?? tok)
    .join(' ');
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
  const srcStatedStrength = src.name ? extractStatedStrength(src.name) : null;
  const entStatedStrength = ent.name ? extractStatedStrength(ent.name) : null;
  if (srcStatedStrength && entStatedStrength && srcStatedStrength !== entStatedStrength) {
    return {
      status: 'red',
      reasonCode: 'drug_mismatch',
      explanation: `Drug does not match: strength stated in the drug names differs (${srcStatedStrength} vs ${entStatedStrength}).`
    };
  }

  const srcConceptViaNdc = srcNdc ? provider.getConcept(srcNdc.normalized11) : null;
  const entConceptViaNdc = entNdc ? provider.getConcept(entNdc.normalized11) : null;
  const srcConcept = srcConceptViaNdc ?? (src.name ? provider.getConcept(src.name) : null);
  const entConcept = entConceptViaNdc ?? (ent.name ? provider.getConcept(ent.name) : null);

  if (!srcConcept || !entConcept) {
    return {
      status: 'yellow',
      reasonCode: 'unknown_drug',
      explanation: 'Could not resolve one or both drugs to a known concept; needs human review.'
    };
  }

  // A side's strength is VERIFIED if the concept came from an NDC (the
  // NDC pins the exact product) or its raw name states a strength. A
  // name-resolved side with no stated strength gives us no basis to
  // claim "same strength" — that claim must not appear in a verdict.
  const srcStrengthVerified = srcConceptViaNdc !== null || srcStatedStrength !== null;
  const entStrengthVerified = entConceptViaNdc !== null || entStatedStrength !== null;
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
