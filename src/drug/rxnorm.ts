/**
 * RxNormDataProvider: the PRIMARY, public-domain source for real
 * generic-equivalence evidence (see src/drug/index.ts's file header,
 * "TO GET PRECISE RXNORM EQUIVALENCE LATER" — this is that follow-on,
 * built against NLM's "Current Prescribable Content" subset instead of
 * requiring a UTS/UMLS account, since that subset needs no license key
 * — see scripts/build-rxnorm-data.ts).
 *
 * Like LocalNdcProvider, this makes ZERO network calls at lookup time:
 * data/rxnorm-data.json.gz is built offline by
 * scripts/build-rxnorm-data.ts (maintainer-run, build-time only) and
 * read into an in-memory Map once per process.
 *
 * GRACEFUL ABSENCE (required by this feature's brief): the data file is
 * new and optional — an older checkout, or a build that hasn't run the
 * script yet, won't have it. RxNormDataProvider must never throw in that
 * case; every lookup just returns null, identical to "this drug isn't in
 * the dataset" and falling through to the engine's existing behavior
 * unchanged.
 */

import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { RxNormConcept, RxNormData } from './rxnorm-data-format.js';

export type { RxNormConcept } from './rxnorm-data-format.js';

interface LoadedRxNormDataset {
  concepts: RxNormConcept[];
  ndcIndex: Map<string, number>;
  scdDisplayNames: Map<string, string>;
}

const EMPTY_DATASET: LoadedRxNormDataset = {
  concepts: [],
  ndcIndex: new Map(),
  scdDisplayNames: new Map()
};

let cachedDataset: LoadedRxNormDataset | null = null;

function defaultDataPath(): string {
  const here = path.dirname(fileURLToPath(import.meta.url));
  return path.join(here, '..', '..', 'data', 'rxnorm-data.json.gz');
}

function loadDataset(dataPath: string): LoadedRxNormDataset {
  try {
    const gz = readFileSync(dataPath);
    const json = gunzipSync(gz).toString('utf8');
    const parsed = JSON.parse(json) as RxNormData;
    return {
      concepts: parsed.concepts,
      ndcIndex: new Map(Object.entries(parsed.ndcIndex)),
      scdDisplayNames: new Map(Object.entries(parsed.scdDisplayNames ?? {}))
    };
  } catch {
    // File missing (feature not built yet on this checkout), or
    // corrupt/unreadable — either way, this is optional evidence, not a
    // required dependency. Fail open to "no RxNorm data available" so
    // the rest of the engine is completely unaffected. See this file's
    // header, "GRACEFUL ABSENCE".
    return EMPTY_DATASET;
  }
}

export class RxNormDataProvider {
  private readonly concepts: RxNormConcept[];
  private readonly ndcIndex: Map<string, number>;
  private readonly scdDisplayNames: Map<string, string>;

  constructor(dataPath?: string) {
    const isDefaultPath = dataPath === undefined;
    const resolvedPath = dataPath ?? defaultDataPath();
    const dataset = isDefaultPath && cachedDataset ? cachedDataset : loadDataset(resolvedPath);
    if (isDefaultPath) cachedDataset = dataset;
    this.concepts = dataset.concepts;
    this.ndcIndex = dataset.ndcIndex;
    this.scdDisplayNames = dataset.scdDisplayNames;
  }

  /** Looks up a WRITTEN NDC (normalized 11-digit) against the RxNorm
   * index. Returns null when absent — including when the dataset itself
   * is absent (see loadDataset's graceful-absence fallback). */
  getByNdc(ndc11: string): RxNormConcept | null {
    const idx = this.ndcIndex.get(ndc11);
    if (idx === undefined) return null;
    return this.concepts[idx] ?? null;
  }

  /** See RxNormData.scdDisplayNames' doc — works even for an SCD that
   * has no NDC of its own (e.g. an SBD's generic counterpart). */
  getScdDisplayName(scdRxcui: string): string | null {
    return this.scdDisplayNames.get(scdRxcui) ?? null;
  }
}

/**
 * Route/administration words to ignore when comparing an RxNorm dose
 * form (e.g. "extended release oral tablet") against an openFDA dose
 * form (e.g. "tablet, extended release") — the two vocabularies order
 * and spell dose forms differently (RxNorm states the route, openFDA
 * usually doesn't) but otherwise describe the same physical form. A
 * SET/token comparison after removing these words is what lets the two
 * genuinely different vocabularies agree on a real match without ever
 * risking a false one: removing a route word can only ever make two
 * forms MORE likely to compare equal when they already share every
 * other word, never manufacture agreement on the form itself (tablet
 * vs capsule, solution vs suspension, etc. still never match).
 */
const DOSE_FORM_ROUTE_WORDS = new Set([
  'oral', 'topical', 'ophthalmic', 'otic', 'nasal', 'rectal', 'vaginal',
  'transdermal', 'sublingual', 'buccal', 'injectable', 'inhalation',
  'intraperitoneal', 'intratracheal', 'intravesical', 'mucosal', 'mucous',
  'membrane', 'urethral'
]);

function doseFormTokenSet(raw: string): Set<string> {
  const tokens = raw
    .toLowerCase()
    .replace(/,/g, ' ')
    .split(/\s+/)
    .filter(Boolean)
    .filter((t) => !DOSE_FORM_ROUTE_WORDS.has(t));
  return new Set(tokens);
}

/**
 * True when two dose-form strings from DIFFERENT vocabularies (RxNorm's
 * "Extended Release Oral Tablet" style vs openFDA's "TABLET, EXTENDED
 * RELEASE" style — see RxNormConcept.doseForm's doc) describe the same
 * physical form, after folding away route words and comparing the
 * remaining word SETS (order-independent, since the two vocabularies
 * order words differently) for exact equality. Never a fuzzy/partial
 * match — e.g. "tablet" and "extended release tablet" do NOT match
 * (different sets), same conservative posture as this file's other
 * equivalence checks.
 */
export function doseFormsEquivalent(rxnormDoseForm: string, localDoseForm: string): boolean {
  const a = doseFormTokenSet(rxnormDoseForm);
  const b = doseFormTokenSet(localDoseForm);
  if (a.size === 0 || b.size === 0) return false;
  if (a.size !== b.size) return false;
  for (const tok of a) {
    if (!b.has(tok)) return false;
  }
  return true;
}

/**
 * The rxnorm_scd_match comparison itself (see src/drug/index.ts
 * compareDrugs' integration): true when an RxNorm concept (resolved from
 * the WRITTEN NDC) and a LocalNdcProvider-derived concept (resolved from
 * the ENTERED name via the existing openFDA resolveConceptByName) agree
 * on ingredient + strength + dose form, DESPITE coming from two
 * different id spaces and vocabularies (see rxnorm-data-format.ts's
 * header for why they're comparable at all: both sides' ingredient/
 * strength were normalized with the SAME normalizeIngredientName/
 * normalizeStrength functions from scripts/build-drug-data.ts).
 *
 * Ingredient and strength are compared by plain string equality (both
 * sides already share the exact same normalized, semicolon-joined,
 * alphabetized shape) — only dose form needs the token-set reconciliation
 * (doseFormsEquivalent) since RxNorm and openFDA spell dose forms
 * differently.
 */
export function rxNormMatchesLocalConcept(
  rxnorm: Pick<RxNormConcept, 'ingredient' | 'strength' | 'doseForm'>,
  local: { ingredient: string; strength: string; doseForm: string }
): boolean {
  if (!rxnorm.ingredient || !rxnorm.strength || !rxnorm.doseForm) return false;
  if (!local.ingredient || !local.strength || !local.doseForm) return false;
  if (rxnorm.ingredient !== local.ingredient) return false;
  if (rxnorm.strength !== local.strength) return false;
  return doseFormsEquivalent(rxnorm.doseForm, local.doseForm);
}
