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
 * zenthorel, yentaprol, rendafexine) — not real substances.
 *
 * Concepts 8-10 (rendafexine) reproduce, deterministically, the
 * confirmed live false GREEN this fixture was extended to guard against:
 * "Bupropion SR 300 MG" vs "Bupropion XL 300 MG" resolved to the same
 * concept in the real bundle because (a) the real openFDA generic name
 * is "Bupropion HYDROCHLORIDE SR" -- the SALT NAME sits between the
 * ingredient and the qualifier, so a short "bupropion sr" query never
 * prefix-matches that key and instead falls through to the bare,
 * qualifier-blind "bupropion" key (mirrored here as "Rendafex Sulfate
 * SR"/"...XL", reachable only via the 3-token key, vs the bare
 * 1-token "rendafex" key with no qualifier at all), and (b) two records
 * that legitimately DO carry the SR/XL qualifier in their own name can
 * still share an identical ingredient/strength/doseForm triple (openFDA's
 * dosage_form text doesn't distinguish release-rate variants). Real
 * openFDA data doesn't hand back a clean, deterministic case for (b) at
 * any one strength (see the real-bundle tests in
 * local-ndc-provider.test.ts for the (a)-shaped repro against real data
 * instead) — hence synthetic here.
 */
const CONCEPTS: LocalConcept[] = [
  { displayName: 'Zylo', ingredient: 'zylodrine', strength: '5mg', doseForm: 'tablet' }, // 0
  { displayName: 'Zylo', ingredient: 'zylodrine', strength: '10mg', doseForm: 'tablet' }, // 1
  { displayName: 'Bimo', ingredient: 'bimoxatin', strength: '25mg', doseForm: 'capsule' }, // 2
  { displayName: 'Crosstag', ingredient: 'foxamine', strength: '50mg', doseForm: 'tablet' }, // 3 -- ambiguous pair w/ 4
  { displayName: 'Crosstag', ingredient: 'zenthorel', strength: '50mg', doseForm: 'tablet' }, // 4 -- genuinely different drug
  { displayName: 'Yenta', ingredient: 'yentaprol', strength: '5mg', doseForm: 'tablet' }, // 5
  { displayName: 'Yentaprol Generic Co', ingredient: 'yentaprol', strength: '10mg', doseForm: 'tablet' }, // 6
  { displayName: 'Bimo Labs', ingredient: 'bimoxatin', strength: '25mg', doseForm: 'capsule' }, // 7 -- 2nd labeler, same triple as 2
  { displayName: 'Rendafex', ingredient: 'rendafexine', strength: '150mg', doseForm: 'tablet, extended release' }, // 8 -- bare, qualifier-blind record (mirrors the real bare "bupropion" key)
  { displayName: 'Rendafex Sulfate SR', ingredient: 'rendafexine', strength: '150mg', doseForm: 'tablet, extended release' }, // 9 -- genuinely SR, but SAME derived triple as 10
  { displayName: 'Rendafex Sulfate XL', ingredient: 'rendafexine', strength: '150mg', doseForm: 'tablet, extended release' } // 10 -- genuinely XL, SAME derived triple as 9 (the openFDA data-artifact collision)
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
    yentaprol: [6],
    rendafex: [8], // bare key -- only the qualifier-blind record; a short "rendafex sr"/"rendafex xl" query never matches the longer salt-name keys below, exactly like real "bupropion sr" vs "bupropion hydrochloride sr"
    'rendafex sulfate sr': [9],
    'rendafexine sulfate sr': [9], // simulates the same product's generic_name also being indexed
    'rendafex sulfate xl': [10]
  },
  formsByIngredient: {
    zylodrine: ['tablet'],
    bimoxatin: ['capsule'],
    yentaprol: ['tablet'],
    rendafexine: ['tablet, extended release']
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

  describe('release-rate qualifier safety (SR/XL/ER/IR/CR/DR) -- regression for a confirmed live false GREEN', () => {
    it('a stated qualifier (SR) is a MISS against a candidate reached ONLY via a qualifier-blind key -- never silently drops the qualifier', () => {
      // "Rendafex 150mg Tablet" (no qualifier stated) resolves fine --
      // the bare key is a legitimate match for an UNqualified query.
      expect(provider.getConcept('Rendafex 150mg Tablet')?.doseForm).toBe('tablet, extended release');
      // But "Rendafex SR 150mg Tablet" must NOT silently resolve to that
      // same qualifier-blind record just because strength narrows to a
      // single triple -- concept 8's own displayName ("Rendafex") states
      // no qualifier at all, so it can never CONFIRM "SR".
      expect(provider.getConcept('Rendafex SR 150mg Tablet')).toBeNull();
      expect(provider.getConcept('Rendafex XL 150mg Tablet')).toBeNull();
    });

    it('resolves cleanly when the matched nameIndex key itself carries the stated qualifier', () => {
      const sr = provider.getConcept('Rendafex Sulfate SR 150mg Tablet');
      expect(sr?.ingredient).toBe('rendafexine');
      const xl = provider.getConcept('Rendafex Sulfate XL 150mg Tablet');
      expect(xl?.ingredient).toBe('rendafexine');
    });
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

  describe('release-rate qualifier safety -- regression for the confirmed live false GREEN (Bupropion SR vs XL)', () => {
    it('REGRESSION: two sides that EACH cleanly resolve via name, to the identical derived concept, but state DIFFERENT release-rate qualifiers, must NOT green -- openFDA dosage_form does not distinguish SR from XL', () => {
      // Both "Rendafex Sulfate SR 150mg Tablet" and "Rendafex Sulfate XL
      // 150mg Tablet" resolve individually (see the getConcept tests
      // above) to concepts sharing the EXACT SAME ingredient/strength/
      // doseForm -- the real-data artifact this fix guards against. Both
      // state the same "150mg" so the earlier raw-text strength
      // cross-check doesn't intervene first; this isolates the
      // qualifierConflict guard.
      const r = compareDrugs(
        { name: 'Rendafex Sulfate SR 150mg Tablet' },
        { name: 'Rendafex Sulfate XL 150mg Tablet' },
        provider
      );
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('unknown_drug');
      expect(r.explanation).toContain('sr vs xl');
    });

    it('does NOT over-block: two DIFFERENT strings that both confirm the SAME qualifier still GREEN concept_match', () => {
      const r = compareDrugs(
        { name: 'Rendafex Sulfate SR 150mg Tablet' },
        { name: 'Rendafexine Sulfate SR 150mg Tablet' },
        provider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('concept_match');
    });

    it('a query stating NO qualifier at all is completely unaffected by this guard (e.g. the Vraylar/cariprazine acceptance shape has no qualifier)', () => {
      // "Bimo 25mg Capsule" / "Bimoxatin 25mg Capsule" state no release
      // qualifier -- qualifierConflict must be false (both null), so the
      // existing concept_match green from earlier in this file is
      // unaffected by this fix.
      const r = compareDrugs({ name: 'Bimo 25mg Capsule' }, { name: 'Bimoxatin 25mg Capsule' }, provider);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('concept_match');
    });

    it('one-sided qualifier confirmation (SR stated + confirmed on one side, no qualifier at all stated on the other) falls to yellow, never a guessed green', () => {
      // "Rendafex Sulfate SR 150mg Tablet" resolves with a CONFIRMED SR
      // qualifier; "Rendafex 150mg Tablet" (bare, no qualifier stated)
      // ALSO resolves fine on its own (a legitimately unqualified query)
      // to a concept with the SAME derived fields -- but we have no
      // information that the second side is actually SR (it could be
      // XL, or something else entirely; the bare record doesn't say).
      // Mirrors the codebase's existing strengthUnverified precedent:
      // asymmetric confirmation must not be silently treated as a match.
      const r = compareDrugs(
        { name: 'Rendafex Sulfate SR 150mg Tablet' },
        { name: 'Rendafex 150mg Tablet' },
        provider
      );
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('unknown_drug');
      expect(r.explanation).toContain('sr vs none stated');
    });
  });
});
