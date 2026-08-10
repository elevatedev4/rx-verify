import { describe, it, expect } from 'vitest';
import { LocalNdcProvider, compareDrugs } from '../src/drug/index.js';

// All NDCs below are REAL entries from the public openFDA NDC directory
// bundled in data/ndc-data.json.gz (see scripts/build-drug-data.ts) —
// not synthetic. They were picked because they're real, common generic
// products (lisinopril, atorvastatin), not because of anything patient-
// or prescriber-related; this file contains no patient/prescriber data
// of any kind.
const LISINOPRIL_10MG_A = '43063076901'; // lisinopril 10mg tablet, one labeler
const LISINOPRIL_10MG_A_OTHER_PACK = '43063076912'; // same product, different package size
const LISINOPRIL_10MG_B = '50090512000'; // lisinopril 10mg tablet, different labeler/product
const LISINOPRIL_20MG = '31722017831'; // lisinopril 20mg tablet (different strength)
const ATORVASTATIN_10MG = '75834025501'; // atorvastatin 10mg tablet (different ingredient)
const UNKNOWN_NDC = '00000000099'; // not present in the dataset

const provider = new LocalNdcProvider();

describe('LocalNdcProvider (real, local, offline openFDA-derived dataset)', () => {
  it('resolves a known real NDC to the correct drug concept', () => {
    const concept = provider.getConcept(LISINOPRIL_10MG_A);
    expect(concept).not.toBeNull();
    expect(concept?.ingredient).toBe('lisinopril');
    expect(concept?.strength).toBe('10mg');
    expect(concept?.doseForm).toBe('tablet');
  });

  it('resolves a second known real NDC (different labeler) to the same ingredient/strength/form', () => {
    const concept = provider.getConcept(LISINOPRIL_10MG_B);
    expect(concept).not.toBeNull();
    expect(concept?.ingredient).toBe('lisinopril');
    expect(concept?.strength).toBe('10mg');
    expect(concept?.doseForm).toBe('tablet');
  });

  it('returns null (unknown) for an NDC not present in the dataset', () => {
    expect(provider.getConcept(UNKNOWN_NDC)).toBeNull();
  });

  it('makes no network calls: getConcept is a synchronous, purely in-memory lookup', () => {
    // Not a network-inspection test (no sandboxed network available
    // here to assert against) — this is a structural guarantee: the
    // method is synchronous, so it CANNOT be awaiting an HTTP call.
    const result = provider.getConcept(LISINOPRIL_10MG_A);
    expect(result).not.toBeInstanceOf(Promise);
  });
});

describe('compareDrugs with LocalNdcProvider (real data)', () => {
  it('is GREEN on identical real NDC', () => {
    const r = compareDrugs({ ndc: LISINOPRIL_10MG_A }, { ndc: LISINOPRIL_10MG_A }, provider);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is YELLOW pack_size for the same product under a different package NDC', () => {
    const r = compareDrugs({ ndc: LISINOPRIL_10MG_A }, { ndc: LISINOPRIL_10MG_A_OTHER_PACK }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('pack_size');
  });

  it('is YELLOW generic_substitution for same ingredient+strength+form, different product/NDC', () => {
    const r = compareDrugs({ ndc: LISINOPRIL_10MG_A }, { ndc: LISINOPRIL_10MG_B }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('generic_substitution');
  });

  it('is RED (never a false green/yellow-equivalence) for a different ingredient', () => {
    const r = compareDrugs({ ndc: LISINOPRIL_10MG_A }, { ndc: ATORVASTATIN_10MG }, provider);
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('drug_mismatch');
    expect(r.explanation).toMatch(/ingredient/);
  });

  it('is RED (never treated as a generic sub) for the same ingredient at a different strength', () => {
    const r = compareDrugs({ ndc: LISINOPRIL_10MG_A }, { ndc: LISINOPRIL_20MG }, provider);
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('drug_mismatch');
    expect(r.explanation).toMatch(/strength/);
  });

  it('is YELLOW unknown_drug (never green/red) when one side is not in the dataset', () => {
    const r = compareDrugs({ ndc: LISINOPRIL_10MG_A }, { ndc: UNKNOWN_NDC }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
  });
});

describe('LocalNdcProvider NAME resolution + compareDrugs concept_match (real openFDA data, live-report acceptance pairs)', () => {
  it('ACCEPTANCE: "CARIPRAZINE 1.5 MG ORAL CAPSULE" vs "Vraylar 1.5 Mg Capsule" is GREEN concept_match (brand+generic of the same product record)', () => {
    const r = compareDrugs({ name: 'CARIPRAZINE 1.5 MG ORAL CAPSULE' }, { name: 'Vraylar 1.5 Mg Capsule' }, provider);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('concept_match');
    expect(r.explanation).toContain('cariprazine');
  });

  it('ACCEPTANCE: "ELIQUIS 5 MG TABLET" vs "Eliquis 5 Mg Tablet" is GREEN (routes cleanly, whichever fast path gets there first)', () => {
    const r = compareDrugs({ name: 'ELIQUIS 5 MG TABLET' }, { name: 'Eliquis 5 Mg Tablet' }, provider);
    expect(r.status).toBe('green');
  });

  it('ACCEPTANCE: the amphetamine-family hand-coded fold already greens "AMPHETAMINE-DEXTROAMPHETAMINE 7.5 MG ORAL TABLET" vs "Dextroamp-Amphetam 7.5 Mg Tab" — new concept path does not fight it', () => {
    const r = compareDrugs(
      { name: 'AMPHETAMINE-DEXTROAMPHETAMINE 7.5 MG ORAL TABLET' },
      { name: 'Dextroamp-Amphetam 7.5 Mg Tab' },
      provider
    );
    expect(r.status).toBe('green');
  });

  it('NEGATIVE GUARD: "Vraylar 1.5 Mg" vs "CARIPRAZINE 3 MG" is NOT green — differing stated strength', () => {
    const r = compareDrugs({ name: 'Vraylar 1.5 Mg' }, { name: 'CARIPRAZINE 3 MG' }, provider);
    expect(r.status).not.toBe('green');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('drug_mismatch');
  });

  it('NEGATIVE GUARD: "Amphetamine Sulfate" vs the dextro-amphet combo is NOT green through the concept path either', () => {
    const r = compareDrugs(
      { name: 'Amphetamine Sulfate 10 mg tablet' },
      { name: 'Dextroamp-Amphet 10 mg tablet' },
      provider
    );
    expect(r.status).not.toBe('green');
  });

  it('NEGATIVE GUARD: an ambiguously-resolving brand name falls through to yellow, never a guess', () => {
    // "Percocet" collides across multiple distinct oxycodone/acetaminophen
    // strength combinations in openFDA; asking with NO strength stated at
    // all must miss (never guess a strength), not silently pick one.
    expect(provider.getConcept('Percocet Tablet')).toBeNull();
  });

  it('name resolution is a heuristic layer only: a name-only query with no NDC on either side never regresses to red for an unresolvable pair', () => {
    const r = compareDrugs({ name: 'Totally Unknown Fabricated Drug Name 5 mg tablet' }, { name: 'Another Made Up Drug 5 mg tablet' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
  });
});

describe('LocalNdcProvider ingredient+strength+form equivalence key (approximation, not real RxNorm rxcui)', () => {
  it('gives the same rxcui-equivalent key to two different NDCs with matching ingredient/strength/form', () => {
    const a = provider.getConcept(LISINOPRIL_10MG_A);
    const b = provider.getConcept(LISINOPRIL_10MG_B);
    expect(a?.rxcui).toBe(b?.rxcui);
  });

  it('gives a different key when the ingredient differs', () => {
    const a = provider.getConcept(LISINOPRIL_10MG_A);
    const c = provider.getConcept(ATORVASTATIN_10MG);
    expect(a?.rxcui).not.toBe(c?.rxcui);
  });

  it('gives a different key when the strength differs', () => {
    const a = provider.getConcept(LISINOPRIL_10MG_A);
    const d = provider.getConcept(LISINOPRIL_20MG);
    expect(a?.rxcui).not.toBe(d?.rxcui);
  });
});
