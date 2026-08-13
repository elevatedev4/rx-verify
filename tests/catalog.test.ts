import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import {
  CatalogDataProvider,
  normalizeCatalogText,
  catalogNameIndexKeys,
  resolveGcnByName
} from '../src/drug/catalog.js';
import type { CatalogData, CatalogEntry } from '../src/drug/catalog-data-format.js';

// Fully INVENTED catalog fixture — never real wholesaler rows (see
// scripts/build-catalog-data.ts's header and this feature's brief:
// "catalog fixtures must be invented"). Drug names are the same
// synthetic vocabulary used elsewhere in this test suite (zylodrine,
// bimoxatin) plus a metoprolol-shaped pair specifically to exercise the
// tartrate/succinate non-equivalence this feature must never blur.
const ENTRY_ZYLO_GENERIC: CatalogEntry = {
  gcn: '5001',
  description: 'ZYLODRINE TAB 10MG GX 100',
  brandGeneric: 'generic',
  deaSchedule: '0'
};
const ENTRY_ZYLO_GENERIC_OTHER_LABELER: CatalogEntry = {
  gcn: '5001',
  description: 'ZYLODRINE TAB 10MG APO 100',
  brandGeneric: 'generic',
  deaSchedule: '0'
};
const ENTRY_ZYLO_BRAND: CatalogEntry = {
  gcn: '5001',
  description: 'ZYLEXA TAB 10MG PFIZ 100',
  brandGeneric: 'brand',
  deaSchedule: '0'
};
// Genuinely different strength -> different GCN in reality.
const ENTRY_ZYLO_20MG: CatalogEntry = {
  gcn: '5002',
  description: 'ZYLODRINE TAB 20MG GX 100',
  brandGeneric: 'generic',
  deaSchedule: '0'
};
// Metoprolol tartrate vs succinate -- different salts, different GCNs,
// must never be blurred together by this feature (see this branch's
// IRON RULE: drug NAMES must never produce a false green).
const ENTRY_METOPROLOL_TARTRATE: CatalogEntry = {
  gcn: '6001',
  description: 'METOPROLOL TARTRATE TAB 50MG GX 100',
  brandGeneric: 'generic',
  deaSchedule: '0'
};
const ENTRY_METOPROLOL_SUCCINATE: CatalogEntry = {
  gcn: '6002',
  description: 'METOPROLOL SUCCINATE ER TAB 50MG GX 100',
  brandGeneric: 'generic',
  deaSchedule: '0'
};
// Two DIFFERENT drugs sharing an ambiguous short description prefix --
// the nameIndex should carry both under whatever short key they collide
// on, and resolution must refuse to guess.
const ENTRY_AMBIGUOUS_A: CatalogEntry = {
  gcn: '7001',
  description: 'CROSSTAG TAB 50MG FOXA 100',
  brandGeneric: 'generic',
  deaSchedule: '0'
};
const ENTRY_AMBIGUOUS_B: CatalogEntry = {
  gcn: '7002',
  description: 'CROSSTAG TAB 50MG ZENT 100',
  brandGeneric: 'generic',
  deaSchedule: '0'
};
// No GCN at all (some real catalog rows carry none).
const ENTRY_NO_GCN: CatalogEntry = {
  gcn: null,
  description: 'BIMOXATIN CAP 25MG NOGCN 30',
  brandGeneric: 'generic',
  deaSchedule: '0'
};

function nameKey(desc: string): string {
  return normalizeCatalogText(desc);
}

const NDC_INDEX: Record<string, CatalogEntry> = {
  '11111000101': ENTRY_ZYLO_GENERIC,
  '11111000102': ENTRY_ZYLO_GENERIC_OTHER_LABELER,
  '22222000201': ENTRY_ZYLO_BRAND,
  '11111000201': ENTRY_ZYLO_20MG,
  '33333000101': ENTRY_METOPROLOL_TARTRATE,
  '33333000201': ENTRY_METOPROLOL_SUCCINATE,
  '44444000101': ENTRY_AMBIGUOUS_A,
  '44444000201': ENTRY_AMBIGUOUS_B,
  '55555000101': ENTRY_NO_GCN
};

// Mirrors scripts/build-catalog-data.ts's real nameIndex construction:
// EVERY leading-prefix key of the normalized description, not just the
// full string — see catalogNameIndexKeys' doc.
const NAME_INDEX: Record<string, string[]> = {};
for (const [ndc, entry] of Object.entries(NDC_INDEX)) {
  for (const key of catalogNameIndexKeys(nameKey(entry.description))) {
    const bucket = (NAME_INDEX[key] ??= []);
    if (bucket[bucket.length - 1] !== ndc) bucket.push(ndc);
  }
}

const DATA: CatalogData = {
  generatedAt: '2026-01-01T00:00:00.000Z',
  source: 'synthetic-fixture',
  attribution: 'synthetic-fixture',
  ndcIndex: NDC_INDEX,
  nameIndex: NAME_INDEX,
  gcnCounts: { '5001': 3, '5002': 1, '6001': 1, '6002': 1, '7001': 1, '7002': 1 }
};

let dataPath: string;
let workDir: string;
let provider: CatalogDataProvider;

beforeAll(() => {
  workDir = mkdtempSync(path.join(tmpdir(), 'rx-verify-catalog-fixture-'));
  dataPath = path.join(workDir, 'fixture.json.gz');
  writeFileSync(dataPath, gzipSync(Buffer.from(JSON.stringify(DATA), 'utf8')));
  provider = new CatalogDataProvider(dataPath);
});

afterAll(() => {
  rmSync(workDir, { recursive: true, force: true });
});

describe('CatalogDataProvider.getByNdc', () => {
  it('resolves a known NDC to its entry', () => {
    expect(provider.getByNdc('11111000101')?.gcn).toBe('5001');
  });

  it('returns null for an unknown NDC', () => {
    expect(provider.getByNdc('00000000000')).toBeNull();
  });

  it('GRACEFUL ABSENCE: a missing data file never throws, every lookup just misses', () => {
    const missing = new CatalogDataProvider(path.join(workDir, 'does-not-exist.json.gz'));
    expect(() => missing.getByNdc('11111000101')).not.toThrow();
    expect(missing.getByNdc('11111000101')).toBeNull();
    expect(missing.resolveGcnByName('Zylodrine Tab 10Mg')).toBeNull();
  });
});

describe('resolveGcnByName (mirrors resolveConceptByName\'s conservatism)', () => {
  it('resolves an exact normalized description match to its GCN', () => {
    expect(provider.resolveGcnByName('Zylodrine Tab 10Mg Gx 100')).toBe('5001');
  });

  it('resolves across different labelers sharing the SAME GCN as one unambiguous answer', () => {
    // Both ZYLO_GENERIC and ZYLO_GENERIC_OTHER_LABELER normalize
    // differently (different trailing labeler token: "gx" vs "apo") so
    // this exercises the SHORTER shared prefix key instead of an exact
    // full-string hit.
    const gcn = provider.resolveGcnByName('Zylodrine Tab 10Mg');
    expect(gcn).toBe('5001');
  });

  it('narrows by stated strength: a 20mg query never resolves to the 10mg (different GCN) entries', () => {
    expect(provider.resolveGcnByName('Zylodrine Tab 20Mg')).toBe('5002');
  });

  it('a stated strength matching NONE of the candidates is a miss, not a fallback guess', () => {
    expect(provider.resolveGcnByName('Zylodrine Tab 999Mg')).toBeNull();
  });

  it('metoprolol tartrate never resolves to the succinate GCN, or vice versa', () => {
    expect(provider.resolveGcnByName('Metoprolol Tartrate 50 Mg Tablet')).toBe('6001');
    expect(provider.resolveGcnByName('Metoprolol Succinate Er 50 Mg Tablet')).toBe('6002');
  });

  it('an ambiguous shared prefix across two genuinely different drugs resolves to null', () => {
    // "crosstag tab 50mg" is a shared 3-token prefix of BOTH ambiguous
    // entries (which differ only in their trailing labeler token) --
    // neither stated strength nor release qualifier disambiguates them,
    // so this must refuse to guess.
    expect(provider.resolveGcnByName('Crosstag Tab 50Mg')).toBeNull();
  });

  it('a candidate with no GCN at all never resolves (nothing to return)', () => {
    expect(provider.resolveGcnByName('Bimoxatin Cap 25Mg Nogcn 30')).toBeNull();
  });

  it('returns null for a name with no token overlap with the catalog at all', () => {
    expect(provider.resolveGcnByName('Totally Unrelated Drug 5 Mg Tablet')).toBeNull();
  });

  it('returns null for an empty/whitespace name', () => {
    expect(resolveGcnByName('   ', NAME_INDEX_AS_MAP(), NDC_INDEX_AS_MAP())).toBeNull();
  });
});

function NAME_INDEX_AS_MAP(): Map<string, string[]> {
  return new Map(Object.entries(NAME_INDEX));
}
function NDC_INDEX_AS_MAP(): Map<string, CatalogEntry> {
  return new Map(Object.entries(NDC_INDEX));
}

describe('normalizeCatalogText', () => {
  it('strips wholesaler "@" truncation markers before normalizing (real export example)', () => {
    // "TB" is not a recognized DOSAGE_FORM_WORDS abbreviation (only
    // tab/tabs/tablets are) -- it's left as-is, same as the real,
    // un-folded catalog data; this test only asserts the "@" strip +
    // digit/unit spacing, not a dosage-form fold that doesn't apply here.
    expect(normalizeCatalogText('ABACAV LAM TB 600 300MG CIP30@')).toBe('abacav lam tb 600 300 mg cip30');
  });

  it('is the SAME normalizer LocalNdcProvider uses (shared build+runtime discipline) modulo the @/# strip', () => {
    // "tab" folds to "tablet" via the shared normalizeDrugNameString, and
    // "10mg" gets a forced space before its unit ("10 mg") -- confirms
    // this file reuses that normalizer rather than a bespoke one.
    expect(normalizeCatalogText('Zylodrine Tab 10Mg')).toBe('zylodrine tablet 10 mg');
  });
});
