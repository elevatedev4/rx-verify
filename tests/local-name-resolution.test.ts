import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { LocalNdcProvider, compareDrugs } from '../src/drug/index.js';
import type { LocalConcept, LocalDrugData } from '../src/drug/local-data-format.js';

/**
 * Small, fully SYNTHETIC LocalDrugData fixture (written to a temp gz
 * file, loaded via LocalNdcProvider's custom-dataPath constructor arg —
 * see src/drug/index.ts, LocalNdcProvider's "isDefaultPath" branch).
 * Deliberately hand-built rather than reusing the real
 * data/ndc-data.json.gz bundle: the ambiguity/miss rules below need
 * EXACT, deterministic control over which names collide and which
 * don't, which real openFDA data can't guarantee will stay true across
 * a future rebuild.
 *
 * All drug names here are invented (zylodrine, bimoxatin, foxamine,
 * zenthorel, yentaprol) — not real substances.
 */
const CONCEPTS: LocalConcept[] = [
  { displayName: 'Zylo', ingredient: 'zylodrine', strength: '5mg', doseForm: 'tablet' }, // 0
  { displayName: 'Zylo', ingredient: 'zylodrine', strength: '10mg', doseForm: 'tablet' }, // 1
  { displayName: 'Bimo', ingredient: 'bimoxatin', strength: '25mg', doseForm: 'capsule' }, // 2
  { displayName: 'Crosstag', ingredient: 'foxamine', strength: '50mg', doseForm: 'tablet' }, // 3 -- ambiguous pair w/ 4
  { displayName: 'Crosstag', ingredient: 'zenthorel', strength: '50mg', doseForm: 'tablet' }, // 4 -- genuinely different drug
  { displayName: 'Yenta', ingredient: 'yentaprol', strength: '5mg', doseForm: 'tablet' }, // 5
  { displayName: 'Yentaprol Generic Co', ingredient: 'yentaprol', strength: '10mg', doseForm: 'tablet' }, // 6
  { displayName: 'Bimo Labs', ingredient: 'bimoxatin', strength: '25mg', doseForm: 'capsule' } // 7 -- 2nd labeler, same triple as 2
];

const DATA: LocalDrugData = {
  generatedAt: '2026-01-01T00:00:00.000Z',
  source: 'synthetic-fixture',
  concepts: CONCEPTS,
  ndcIndex: {
    '00000000001': 2 // Bimo 25mg capsule, pinned by NDC
  },
  nameIndex: {
    zylo: [0, 1],
    zylodrine: [0, 1],
    bimo: [2, 7],
    bimoxatin: [2, 7],
    crosstag: [3, 4],
    foxamine: [3],
    zenthorel: [4],
    yenta: [5],
    yentaprol: [6]
  },
  formsByIngredient: {
    zylodrine: ['tablet'],
    bimoxatin: ['capsule'],
    yentaprol: ['tablet']
  }
};

let dataPath: string;
let workDir: string;
let provider: LocalNdcProvider;

beforeAll(() => {
  workDir = mkdtempSync(path.join(tmpdir(), 'rx-verify-name-fixture-'));
  dataPath = path.join(workDir, 'fixture.json.gz');
  writeFileSync(dataPath, gzipSync(Buffer.from(JSON.stringify(DATA), 'utf8')));
  provider = new LocalNdcProvider(dataPath);
});

afterAll(() => {
  rmSync(workDir, { recursive: true, force: true });
});

describe('LocalNdcProvider.getConcept name resolution (synthetic fixture)', () => {
  it('resolves a brand name + stated strength to the single matching candidate', () => {
    const c = provider.getConcept('Zylo 5mg Tablet');
    expect(c?.ingredient).toBe('zylodrine');
    expect(c?.strength).toBe('5mg');
  });

  it('resolves the generic name identically to the brand name for the same strength', () => {
    const c = provider.getConcept('Zylodrine 5mg Tablet');
    expect(c?.ingredient).toBe('zylodrine');
    expect(c?.strength).toBe('5mg');
  });

  it('is a MISS (null) when a stated strength matches none of the candidates for that name — never guesses the wrong strength', () => {
    expect(provider.getConcept('Zylo 999mg Tablet')).toBeNull();
  });

  it('is a MISS (null) when no strength is stated and the name has MULTIPLE distinct strengths on record — cannot pick one', () => {
    expect(provider.getConcept('Zylo Tablet')).toBeNull();
  });

  it('resolves with no stated strength when every candidate for the name collapses to the SAME distinct (ingredient,strength,form) triple (many labelers, one drug)', () => {
    const c = provider.getConcept('Bimo Capsule');
    expect(c?.ingredient).toBe('bimoxatin');
    expect(c?.strength).toBe('25mg');
  });

  it('is a MISS (null) for a name not present in the index at all', () => {
    expect(provider.getConcept('Zorbaxatin 10mg Tablet')).toBeNull();
  });

  it('AMBIGUOUS: is a MISS (null) when a shared name maps to genuinely DIFFERENT ingredients that both match the stated strength — never guesses either one', () => {
    expect(provider.getConcept('Crosstag 50mg Tablet')).toBeNull();
  });

  it('AMBIGUOUS: each of the colliding brand name\'s own distinct generic names still resolves fine on its own (the collision is specific to the shared brand key)', () => {
    expect(provider.getConcept('Foxamine 50mg Tablet')?.ingredient).toBe('foxamine');
    expect(provider.getConcept('Zenthorel 50mg Tablet')?.ingredient).toBe('zenthorel');
  });

  it('NDC lookup is unaffected by name lookup existing (parseable-NDC input never falls through to name matching)', () => {
    const c = provider.getConcept('00000000001');
    expect(c?.ingredient).toBe('bimoxatin');
  });

  it('knownFormsFor reports the known forms for an ingredient key, or null when untracked', () => {
    expect(provider.knownFormsFor('zylodrine')).toEqual(['tablet']);
    expect(provider.knownFormsFor('does-not-exist')).toBeNull();
  });
});

describe('compareDrugs concept_match GREEN (synthetic fixture)', () => {
  it('is GREEN concept_match for brand vs generic name resolving to the identical concept', () => {
    const r = compareDrugs({ name: 'Zylo 5mg Tablet' }, { name: 'Zylodrine 5mg Tablet' }, provider);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('concept_match');
    expect(r.explanation).toContain('zylodrine');
  });

  it('is GREEN concept_match for one side pinned by NDC and the other resolved by name to the same ingredient/strength/form', () => {
    const r = compareDrugs({ ndc: '00000000001' }, { name: 'Bimoxatin 25mg Capsule' }, provider);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('concept_match');
  });

  it('does NOT green when the stated strengths genuinely differ (caught by the existing raw-text cross-check, unrelated to concept resolution)', () => {
    const r = compareDrugs({ name: 'Zylo 5mg Tablet' }, { name: 'Zylodrine 10mg Tablet' }, provider);
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('drug_mismatch');
  });

  it('IRON RULE: an ambiguous name-resolved side falls through to YELLOW unknown_drug, never red, never green', () => {
    // Both sides state the SAME "50mg" so the earlier raw-text stated-
    // strength cross-check (unrelated to concept resolution) doesn't
    // fire first — this isolates the ambiguous-name-resolution path.
    const r = compareDrugs({ name: 'Crosstag 50mg Tablet' }, { name: 'Zenthorel 50mg Tablet' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
  });

  it('IRON RULE: two cleanly-resolved-by-name but genuinely DIFFERENT ingredients stay YELLOW (never a new red from name resolution alone)', () => {
    const r = compareDrugs({ name: 'Foxamine 50mg Tablet' }, { name: 'Zenthorel 50mg Tablet' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
  });

  it('formsByIngredient confirmation note appears in the fallback yellow when ingredients match but strength differs and the ingredient has exactly one known form', () => {
    // Neither side states a strength in raw text (each name is still
    // unambiguous on its own — 'yenta' and 'yentaprol' each map to only
    // ONE candidate), so this isolates the same-ingredient/different-
    // strength concept-resolution path from the earlier raw-text
    // stated-strength cross-check.
    const r = compareDrugs({ name: 'Yenta Tablet' }, { name: 'Yentaprol Tablet' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
    expect(r.explanation).toContain('yentaprol is only known to come as tablet');
  });

  it('two NDC-resolved sides never enter name-resolution logic at all (existing pack_size/generic_substitution/red paths fully untouched)', () => {
    const r = compareDrugs({ ndc: '00000000001' }, { ndc: '00000000001' }, provider);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });
});
