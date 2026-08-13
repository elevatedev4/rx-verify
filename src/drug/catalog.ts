/**
 * CatalogDataProvider: the SECONDARY, internal-use-only source of
 * generic-equivalence evidence — the pharmacy's own wholesaler catalog
 * (see src/drug/catalog-data-format.ts's header for the licensing note:
 * GCN is First DataBank proprietary reference data, owner-supplied for
 * his own tool, never redistributed).
 *
 * Same offline/graceful-absence contract as RxNormDataProvider
 * (src/drug/rxnorm.ts) and LocalNdcProvider (src/drug/index.ts): zero
 * network calls at lookup time, and a missing/unreadable
 * data/catalog-data.json.gz degrades to "no catalog data available"
 * rather than throwing.
 */

import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  normalizeDrugNameString,
  extractStatedStrength,
  extractStatedConcentrationStrength,
  extractReleaseQualifier
} from './index.js';
import type { CatalogData, CatalogEntry } from './catalog-data-format.js';

export type { CatalogEntry } from './catalog-data-format.js';

/**
 * Normalizes a wholesaler catalog description (or, at lookup time, a
 * free-text entered drug name being resolved against the catalog) for
 * name-matching. Reuses normalizeDrugNameString (the SAME normalizer
 * LocalNdcProvider's resolveConceptByName and its nameIndex both use —
 * src/drug/index.ts) so the matching discipline is identical, plus
 * stripping the wholesaler-specific "@"/"#" noise symbols seen in real
 * catalog exports (e.g. a trailing "@" on a truncated description)
 * BEFORE that shared normalizer runs. Exported so
 * scripts/build-catalog-data.ts can build data/catalog-data.json.gz's
 * nameIndex with the EXACT same function this file's lookup side uses —
 * sharing one normalizer is what keeps build-time keys and runtime
 * queries comparable (same rationale as extractNameKeys' doc in
 * scripts/build-drug-data.ts).
 */
export function normalizeCatalogText(raw: string): string {
  return normalizeDrugNameString(raw.replace(/[@#]/g, ' '));
}

interface LoadedCatalogDataset {
  ndcIndex: Map<string, CatalogEntry>;
  nameIndex: Map<string, string[]>;
}

const EMPTY_DATASET: LoadedCatalogDataset = { ndcIndex: new Map(), nameIndex: new Map() };

let cachedDataset: LoadedCatalogDataset | null = null;

function defaultDataPath(): string {
  const here = path.dirname(fileURLToPath(import.meta.url));
  return path.join(here, '..', '..', 'data', 'catalog-data.json.gz');
}

function loadDataset(dataPath: string): LoadedCatalogDataset {
  try {
    const gz = readFileSync(dataPath);
    const json = gunzipSync(gz).toString('utf8');
    const parsed = JSON.parse(json) as CatalogData;
    return {
      ndcIndex: new Map(Object.entries(parsed.ndcIndex)),
      nameIndex: new Map(Object.entries(parsed.nameIndex ?? {}))
    };
  } catch {
    // Missing/unreadable file -- optional evidence, fail open. See
    // RxNormDataProvider's identical rationale in src/drug/rxnorm.ts.
    return EMPTY_DATASET;
  }
}

/**
 * Max leading-token-prefix length tried against the catalog nameIndex
 * (see resolveGcnByName below) — LARGER than LocalNdcProvider's
 * MAX_NAME_KEY_TOKENS (4) because that constant bounds brand/generic
 * ALIAS length (typically 1-2 words), whereas a catalog nameIndex key is
 * an entire normalized wholesaler DESCRIPTION (commonly 5-8 words, e.g.
 * "abacavir lamivudine tablet 600 300mg cip30") — a shorter cap would
 * make most of the catalog structurally unreachable regardless of how
 * well the entered name matches.
 */
export const MAX_CATALOG_NAME_KEY_TOKENS = 10;

/**
 * Every candidate nameIndex key for one normalized catalog description —
 * ALL leading-token prefixes from length 1 up to
 * MAX_CATALOG_NAME_KEY_TOKENS, not just the full string. This is what
 * makes resolveGcnByName's query-side prefix trial (below) actually
 * reachable: unlike LocalNdcProvider's nameIndex (whose keys are already
 * SHORT aliases — a brand_name/generic_name field, typically 1-2 words —
 * so trying the QUERY's leading prefixes against those short keys
 * naturally lines up), a catalog description is one long blob where the
 * drug-name portion is only its OWN leading substring, with strength/
 * form/pack/labeler detail trailing after it that a human-typed entered
 * name won't restate verbatim. Indexing every prefix means a query whose
 * own leading words happen to match the catalog description's leading
 * words — the realistic case, e.g. "Zylodrine Tab 10Mg" against a
 * description that continues "...GX 100" — still finds it, without ever
 * matching on anything OTHER than a genuine shared leading substring
 * (still exact string equality at whatever length hits; never fuzzy).
 * Used by scripts/build-catalog-data.ts to build nameIndex; exported
 * here (rather than duplicated there) so the build-time key generation
 * and this file's MAX_CATALOG_NAME_KEY_TOKENS-bounded lookup trial can
 * never drift apart.
 */
export function catalogNameIndexKeys(normalizedText: string): string[] {
  const tokens = normalizedText.split(' ').filter(Boolean);
  const keys: string[] = [];
  for (let len = 1; len <= Math.min(MAX_CATALOG_NAME_KEY_TOKENS, tokens.length); len++) {
    keys.push(tokens.slice(0, len).join(' '));
  }
  return keys;
}

/**
 * Resolve a free-text drug name to AT MOST ONE unambiguous GCN via the
 * wholesaler catalog's nameIndex, or null. Deliberately mirrors
 * resolveConceptByName's conservative-by-design structure (see
 * src/drug/index.ts, lines documented as "study... apply the same
 * discipline"): find candidates by longest-prefix-first key lookup,
 * narrow by a stated strength and then a stated release-rate qualifier
 * (matched against text EXTRACTED FROM each candidate's raw description,
 * since CatalogEntry has no structured strength/qualifier field the way
 * LocalConcept does), then require the survivors collapse to exactly one
 * DISTINCT non-empty GCN. Any doubt at any stage — no candidates, a
 * stated strength/qualifier that confirms none of them, more than one
 * distinct GCN among the survivors — returns null, never a guess.
 *
 * KNOWN LIMITATION, expected and accepted: wholesaler descriptions are
 * aggressively abbreviated ("ABACAV LAM TB 600 300MG CIP30@") and rarely
 * share enough normalized vocabulary with a human-typed PioneerRx entry
 * ("Abacavir-Lamivudine 600-300 Mg Tablet") to reach a nameIndex hit at
 * all — this path is expected to fire relatively rarely in practice. A
 * miss here is always safe (falls through to whatever verdict the rest
 * of the engine already reached) and never itself a source of a false
 * result, so low recall is an acceptable, documented tradeoff for a
 * feature whose only job is to occasionally UPGRADE an existing
 * yellow/unknown to green on solid evidence — see compareDrugs'
 * integration of this in src/drug/index.ts.
 */
export function resolveGcnByName(
  rawName: string,
  nameIndex: Map<string, string[]>,
  ndcIndex: Map<string, CatalogEntry>
): string | null {
  const normalized = normalizeCatalogText(rawName);
  if (!normalized) return null;
  const tokens = normalized.split(' ').filter(Boolean);
  if (tokens.length === 0) return null;

  let candidateNdcs: string[] | undefined;
  for (let len = Math.min(MAX_CATALOG_NAME_KEY_TOKENS, tokens.length); len >= 1; len--) {
    const key = tokens.slice(0, len).join(' ');
    const hit = nameIndex.get(key);
    if (hit && hit.length > 0) {
      candidateNdcs = hit;
      break;
    }
  }
  if (!candidateNdcs) return null;

  let candidates = candidateNdcs
    .map((ndc) => ndcIndex.get(ndc))
    .filter((e): e is CatalogEntry => e !== undefined);
  if (candidates.length === 0) return null;

  const extractStrength = (text: string): string | null => {
    const conc = extractStatedConcentrationStrength(text);
    return conc ?? extractStatedStrength(text);
  };

  const statedStrength = extractStrength(rawName);
  if (statedStrength) {
    const narrowed = candidates.filter((c) => extractStrength(c.description) === statedStrength);
    if (narrowed.length === 0) return null; // stated strength confirms NONE of the candidates -> miss, not a guess
    candidates = narrowed;
  }

  const statedQualifier = extractReleaseQualifier(rawName);
  if (statedQualifier) {
    const confirmed = candidates.filter((c) => extractReleaseQualifier(c.description) === statedQualifier);
    if (confirmed.length === 0) return null;
    candidates = confirmed;
  }

  const distinctGcns = new Set(
    candidates.map((c) => c.gcn).filter((g): g is string => typeof g === 'string' && g.length > 0)
  );
  if (distinctGcns.size !== 1) return null; // ambiguous (or no candidate had a GCN) -> miss, never a guess

  return [...distinctGcns][0] as string;
}

export class CatalogDataProvider {
  private readonly ndcIndex: Map<string, CatalogEntry>;
  private readonly nameIndex: Map<string, string[]>;

  constructor(dataPath?: string) {
    const isDefaultPath = dataPath === undefined;
    const resolvedPath = dataPath ?? defaultDataPath();
    const dataset = isDefaultPath && cachedDataset ? cachedDataset : loadDataset(resolvedPath);
    if (isDefaultPath) cachedDataset = dataset;
    this.ndcIndex = dataset.ndcIndex;
    this.nameIndex = dataset.nameIndex;
  }

  /** Looks up a WRITTEN NDC (normalized 11-digit) against the catalog.
   * Returns null when absent, including when the dataset itself is
   * absent (graceful absence — see loadDataset). */
  getByNdc(ndc11: string): CatalogEntry | null {
    return this.ndcIndex.get(ndc11) ?? null;
  }

  /** See resolveGcnByName's doc — this is that function bound to this
   * instance's loaded (or gracefully empty) indexes. */
  resolveGcnByName(rawName: string): string | null {
    return resolveGcnByName(rawName, this.nameIndex, this.ndcIndex);
  }
}
