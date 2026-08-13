import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { LocalNdcProvider, compareDrugs, type RxNormProvider, type RxNormEquivalenceProvider } from '../src/drug/index.js';
import type { LocalConcept, LocalDrugData } from '../src/drug/local-data-format.js';

/**
 * Field report (2026-08-13): source "VENLAFAXINE XR (EFFEXOR XR) 37.5 MG
 * CAPSULE" (written NDC also present on the e-script) vs entered
 * "Venlafaxin Er 37.5mg Caps" went yellow unknown_drug. Root cause:
 * Pioneer's free-text entry truncated the ingredient word's trailing
 * letter ("Venlafaxin" for "venlafaxine"). Two independent code paths can
 * confirm this pair matches (see src/drug/index.ts):
 *  - the component-wise name fallback (compareNameComponents /
 *    decomposeDrugNameComponents), exercised here via an
 *    always-null-resolving provider so concept resolution never
 *    intervenes;
 *  - the "equivalence layer" (LocalNdcProvider.getConcept's name path,
 *    resolveConceptByName), exercised here via a small synthetic
 *    LocalDrugData bundle -- this is the REALISTIC path for the actual
 *    live report, since the source side carries a written NDC (resolves
 *    directly, no name parsing needed at all, so the brand name in
 *    parens on the source's raw text never even matters) while the
 *    entered side has no NDC and must resolve by name.
 *
 * Both paths share the same underlying tokensElisionEqual rule (see its
 * doc in src/drug/index.ts for the full false-GREEN adversarial review):
 * two tokens compare equal when one is EXACTLY the other minus its final
 * 1 letter, the longer is >=9 chars, and the shorter is >=8 chars.
 */
describe('trailing-elision tolerance (component fallback)', () => {
  const unresolvedProvider: RxNormProvider = { getConcept: () => null };

  it('is GREEN name_component_match for a Venlafaxine ER pair truncated on the entered side (no brand-name parens)', () => {
    const r = compareDrugs(
      { name: 'Venlafaxine ER 37.5 MG Capsule' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      unresolvedProvider
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('name_component_match');
  });

  it('is GREEN in the reverse direction too', () => {
    const r = compareDrugs(
      { name: 'Venlafaxin Er 37.5mg Caps' },
      { name: 'Venlafaxine ER 37.5 MG Capsule' },
      unresolvedProvider
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('name_component_match');
  });

  it('strength tokenizer already splits number+unit with NO space on the entered side ("37.5mg") -- confirms fix 1b needed no code change', () => {
    // Isolate strength parsing from the elision fix: identical spelling
    // on both sides, only the space-before-unit differs.
    const r = compareDrugs(
      { name: 'Venlafaxine ER 37.5 MG Capsule' },
      { name: 'Venlafaxine Er 37.5mg Capsule' },
      unresolvedProvider
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('name_identity_match');
  });

  it('a genuine DIFFERENT ingredient stays non-green even though the entered name is otherwise identical to the elidable pair', () => {
    const r = compareDrugs(
      { name: 'Sertraline 37.5 MG Capsule' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      unresolvedProvider
    );
    expect(r.status).not.toBe('green');
    expect(r.reasonCode).toBe('unknown_drug');
  });

  it('GATE: a differing strength blocks green even when the ingredient token would otherwise elide-match (elision may only ever CONTRIBUTE toward green alongside a stated-and-equal strength)', () => {
    const r = compareDrugs(
      { name: 'Venlafaxine ER 50 MG Capsule' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      unresolvedProvider
    );
    expect(r.status).not.toBe('green');
    // Caught even earlier, by the raw-text stated-strength cross-check.
    expect(r.reasonCode).toBe('drug_mismatch');
  });

  it('a genuinely different drug/strength pair (same shape as the report) stays non-green -- target outcome\'s second half', () => {
    const r = compareDrugs(
      { name: 'Duloxetine 60 MG Capsule' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      unresolvedProvider
    );
    expect(r.status).not.toBe('green');
  });

  it('another real elidable pair works generally, not just venlafaxine: "Amlodipine 5 MG Tablet" vs "Amlodipin 5mg Tab"', () => {
    const r = compareDrugs({ name: 'Amlodipine 5 MG Tablet' }, { name: 'Amlodipin 5mg Tab' }, unresolvedProvider);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('name_component_match');
  });

  describe('length-gate adversarial checks (false-GREEN hunt)', () => {
    it('tokens shorter than the 8-char floor never elide, even off by exactly one trailing letter', () => {
      // "loratad" (7) / "loratadi" (8) -- shorter is below the 8-char
      // floor, so this must stay a miss, never a guessed green.
      const r = compareDrugs({ name: 'Loratad 10 MG Tablet' }, { name: 'Loratadi 10mg Tab' }, unresolvedProvider);
      expect(r.status).not.toBe('green');
      expect(r.reasonCode).toBe('unknown_drug');
    });

    it('a 2-letter difference (not exactly 1) never elides, even when both tokens are long enough', () => {
      // "venlafaxin" (10) vs "venlafax" (8) -- differs by 2 trailing
      // letters, not 1; must not match despite both being long/short
      // enough on their own.
      const r = compareDrugs({ name: 'Venlafaxine 37.5 MG Capsule' }, { name: 'Venlafax 37.5mg Cap' }, unresolvedProvider);
      expect(r.status).not.toBe('green');
    });

    it('an equal-length substitution (not an elision at all) never matches -- this rule is insert/delete-of-the-final-letter ONLY, never a substitution anywhere in the word', () => {
      // "venlafaxine" vs "venlafaxina" -- same length, last letter
      // substituted, not elided. Must not match (this file's address
      // comparator accepts equal-length substitutions for its own
      // yellow-tier field; drug identity does not).
      const r = compareDrugs(
        { name: 'Venlafaxine 37.5 MG Capsule' },
        { name: 'Venlafaxina 37.5mg Capsule' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
    });

    it('multiple simultaneous eliding tokens on a combo product never match -- only ONE differing token pair per side is ever tolerated', () => {
      // Both ingredient tokens of a 2-ingredient combo truncated at once
      // must NOT be waved through -- ingredientTokenSetsEqual only
      // tolerates exactly one non-matching token on each side.
      const r = compareDrugs(
        { name: 'Hydrocodone Acetaminophen 5/325 Tablet' },
        { name: 'Hydrocodon Acetaminophe 5/325 Tab' },
        unresolvedProvider
      );
      expect(r.status).not.toBe('green');
    });

    it('does not accidentally elide when the two ingredient tokens are simply different words of similar length', () => {
      // "clonidine" (9) vs "cloridine" (9, transposed) -- same length,
      // not an elision shape (longer/shorter must differ in LENGTH by
      // exactly 1) -- must not match.
      const r = compareDrugs({ name: 'Clonidine 0.1 MG Tablet' }, { name: 'Cloridine 0.1mg Tab' }, unresolvedProvider);
      expect(r.status).not.toBe('green');
    });
  });
});

/**
 * Small, fully SYNTHETIC LocalDrugData fixture exercising the
 * "equivalence layer" path -- LocalNdcProvider.getConcept's name-based
 * resolution (resolveConceptByName) -- for the SAME field report. This is
 * the realistic path for the actual live case: the source side resolves
 * via its written NDC directly (never touching the brand name in
 * parens at all), while the entered side has no NDC and must resolve by
 * a truncated free-text name.
 */
describe('trailing-elision tolerance (equivalence layer / resolveConceptByName)', () => {
  const CONCEPTS: LocalConcept[] = [
    // 0: the real reported drug, reachable by its full correct spelling.
    {
      displayName: 'Venlafaxine Hydrochloride ER',
      ingredient: 'venlafaxine hydrochloride',
      strength: '37.5mg',
      doseForm: 'capsule, extended release'
    },
    // 1/2: an UNRELATED pair sharing the same 8-char truncated stem
    // "clonidin" as two DIFFERENT concepts reachable by two DIFFERENT
    // appended letters -- used to prove the ambiguous-across-letters
    // guard refuses to guess (see the 'AMBIGUOUS' test below). Both
    // names are invented for this test only; not real drugs.
    { displayName: 'Clonidina', ingredient: 'clonidina', strength: '0.1mg', doseForm: 'tablet' },
    { displayName: 'Clonidine', ingredient: 'clonidine', strength: '0.1mg', doseForm: 'tablet' }
  ];

  const DATA: LocalDrugData = {
    generatedAt: '2026-01-01T00:00:00.000Z',
    source: 'synthetic-fixture',
    concepts: CONCEPTS,
    ndcIndex: {
      '00093715601': 0 // the reported venlafaxine ER concept, pinned by written NDC
    },
    nameIndex: {
      'venlafaxine hydrochloride': [0],
      venlafaxine: [0],
      clonidina: [1],
      clonidine: [2]
    },
    formsByIngredient: {
      'venlafaxine hydrochloride': ['capsule, extended release']
    }
  };

  let workDir: string;
  let dataPath: string;
  let provider: LocalNdcProvider;

  beforeAll(() => {
    workDir = mkdtempSync(path.join(tmpdir(), 'rx-verify-elision-fixture-'));
    dataPath = path.join(workDir, 'fixture.json.gz');
    writeFileSync(dataPath, gzipSync(Buffer.from(JSON.stringify(DATA), 'utf8')));
    provider = new LocalNdcProvider(dataPath);
  });

  afterAll(() => {
    rmSync(workDir, { recursive: true, force: true });
  });

  it('TARGET OUTCOME: the exact reported pair is GREEN via NDC + elision-tolerant name resolution, even with the brand name in parens on the source side', () => {
    const r = compareDrugs(
      { name: 'VENLAFAXINE XR (EFFEXOR XR) 37.5 MG CAPSULE', ndc: '00093715601' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      provider
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('concept_match');
  });

  it('getConcept resolves the truncated name directly to the correctly-spelled concept', () => {
    const c = provider.getConcept('Venlafaxin Er 37.5mg Caps');
    expect(c?.ingredient).toBe('venlafaxine hydrochloride');
    expect(c?.strength).toBe('37.5mg');
  });

  it('a stated strength that does not match the resolved concept is still a miss (elision never bypasses the existing strength-narrowing safety rule)', () => {
    expect(provider.getConcept('Venlafaxin Er 100mg Caps')).toBeNull();
  });

  it('AMBIGUOUS ACROSS LETTERS: two different concepts each reachable by appending a DIFFERENT letter to the same 8-char stem must never be guessed -- resolves to null (miss)', () => {
    // "clonidin" (8 chars) + "a" -> "clonidina" (concept 1) and
    // "clonidin" + "e" -> "clonidine" (concept 2) are both real nameIndex
    // hits for two DIFFERENT concepts. A query of the bare 8-char stem
    // must refuse to guess between them.
    expect(provider.getConcept('Clonidin 0.1mg Tablet')).toBeNull();
  });

  it('a first token shorter than the 8-char floor never attempts the elision retry', () => {
    // "clonidi" (7 chars) is one letter short of the floor -- must stay a
    // miss even though appending "n" would otherwise reach a real key
    // ("clonidin" is not itself in nameIndex, so this also isn\'t a hit
    // on its own merits -- the point is the retry loop must not even
    // fire for a 7-char first token).
    expect(provider.getConcept('Clonidi 0.1mg Tablet')).toBeNull();
  });

  it('IRON RULE: an entered name that fails to resolve even with elision still falls through to the existing component fallback / unknown_drug yellow, never a red', () => {
    const r = compareDrugs(
      { name: 'VENLAFAXINE XR (EFFEXOR XR) 37.5 MG CAPSULE', ndc: '00093715601' },
      { name: 'Zzznotarealdrugatall 37.5mg Caps' },
      provider
    );
    expect(r.status).not.toBe('red');
  });
});

/**
 * Reviewer follow-up (attack review, same field report): tryEquivalenceUpgrade
 * (src/drug/index.ts) is ANOTHER consumer of provider.getConcept — it calls
 * it on the entered name to compare against the WRITTEN NDC's honest RxNorm
 * evidence (see rxNormMatchesLocalConcept in src/drug/rxnorm.ts). Since
 * resolveConceptByName is now widened with the elision retry above, this
 * locks in that a local concept the elision retry resolves to, but which
 * DOESN'T actually match the honest RxNorm evidence for the written
 * product, is still correctly rejected -- rxNormMatchesLocalConcept's
 * ingredient/strength comparison is a STRICT string equality (see its own
 * doc), so a widened-but-wrong local resolution can never slip through as
 * a green rxnorm_scd_match. The RxNorm side here is the "honest evidence"
 * (a hand-stubbed RxNormEquivalenceProvider standing in for the real,
 * NLM-derived RxNormDataProvider) and is deliberately NOT run through any
 * elision logic of its own -- it's the ground truth this test checks the
 * widened local resolution against.
 */
describe('tryEquivalenceUpgrade: a widened (elision-retried) local concept must still fail honest RxNorm evidence on a genuine mismatch', () => {
  const CONCEPTS: LocalConcept[] = [
    // Reachable ONLY via the elision retry ("venlafaxin" -> "venlafaxine"),
    // and deliberately missing the salt name ("venlafaxine", not
    // "venlafaxine hydrochloride") -- a plausible, but WRONG, resolution:
    // this local dataset's own ingredient string doesn't match the real
    // product's honest ingredient identity.
    { displayName: 'Venlafaxine ER', ingredient: 'venlafaxine', strength: '37.5mg', doseForm: 'capsule, extended release' }
  ];
  const DATA: LocalDrugData = {
    generatedAt: '2026-01-01T00:00:00.000Z',
    source: 'synthetic-fixture',
    concepts: CONCEPTS,
    ndcIndex: {},
    nameIndex: { venlafaxine: [0] },
    formsByIngredient: {}
  };

  let workDir: string;
  let provider: LocalNdcProvider;

  beforeAll(() => {
    workDir = mkdtempSync(path.join(tmpdir(), 'rx-verify-equivalence-fixture-'));
    const dataPath = path.join(workDir, 'fixture.json.gz');
    writeFileSync(dataPath, gzipSync(Buffer.from(JSON.stringify(DATA), 'utf8')));
    provider = new LocalNdcProvider(dataPath);
  });

  afterAll(() => {
    rmSync(workDir, { recursive: true, force: true });
  });

  it('confirms the widened resolver DOES resolve the truncated entered name (so this is a real elision-retry consumer, not a vacuous test)', () => {
    const c = provider.getConcept('Venlafaxin Er 37.5mg Caps');
    expect(c?.ingredient).toBe('venlafaxine');
    expect(c?.strength).toBe('37.5mg');
  });

  it('REGRESSION: honest RxNorm evidence for the written NDC states the full salt name ("venlafaxine hydrochloride") -- the elision-widened local concept\'s bare "venlafaxine" does not match, so this stays yellow, never a false green', () => {
    const honestRxnormEvidence: RxNormEquivalenceProvider = {
      getByNdc: (ndc11) =>
        ndc11 === '00093715601'
          ? {
              rxcui: '900002',
              tty: 'SCD',
              displayName: 'venlafaxine hydrochloride 37.5 MG Extended Release Oral Capsule',
              ingredient: 'venlafaxine hydrochloride',
              strength: '37.5mg',
              doseForm: 'extended release oral capsule'
            }
          : null
    };

    const r = compareDrugs(
      { name: 'Some Unresolvable Brand Name 37.5 MG Capsule', ndc: '00093-7156-01' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      provider,
      { rxnormProvider: honestRxnormEvidence }
    );
    expect(r.status).not.toBe('green');
    expect(r.reasonCode).not.toBe('rxnorm_scd_match');
    expect(r.status).toBe('yellow');
  });

  it('REGRESSION: honest RxNorm evidence with the SAME ingredient string but a DIFFERENT strength is also rejected (strict on BOTH fields, not just ingredient)', () => {
    const honestRxnormEvidenceWrongStrength: RxNormEquivalenceProvider = {
      getByNdc: (ndc11) =>
        ndc11 === '00093715601'
          ? {
              rxcui: '900003',
              tty: 'SCD',
              displayName: 'venlafaxine 75 MG Extended Release Oral Capsule',
              ingredient: 'venlafaxine',
              strength: '75mg',
              doseForm: 'capsule, extended release'
            }
          : null
    };

    const r = compareDrugs(
      { name: 'Some Unresolvable Brand Name 75 MG Capsule', ndc: '00093-7156-01' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      provider,
      { rxnormProvider: honestRxnormEvidenceWrongStrength }
    );
    expect(r.status).not.toBe('green');
    expect(r.reasonCode).not.toBe('rxnorm_scd_match');
  });

  it('sanity (positive control): when the widened local resolution DOES honestly match the RxNorm evidence, it correctly upgrades to green -- proves the strict check isn\'t just always failing closed', () => {
    const matchingRxnormEvidence: RxNormEquivalenceProvider = {
      getByNdc: (ndc11) =>
        ndc11 === '00093715601'
          ? {
              rxcui: '900004',
              tty: 'SCD',
              displayName: 'venlafaxine 37.5 MG Extended Release Oral Capsule',
              ingredient: 'venlafaxine',
              strength: '37.5mg',
              doseForm: 'capsule, extended release'
            }
          : null
    };

    const r = compareDrugs(
      { name: 'Some Unresolvable Brand Name 37.5 MG Capsule', ndc: '00093-7156-01' },
      { name: 'Venlafaxin Er 37.5mg Caps' },
      provider,
      { rxnormProvider: matchingRxnormEvidence }
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('rxnorm_scd_match');
  });
});
