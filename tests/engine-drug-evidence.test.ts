import { describe, it, expect } from 'vitest';
import { verify } from '../src/engine/index.js';
import type { RxNormProvider, RxNormEquivalenceProvider } from '../src/drug/index.js';
import { RxNormDataProvider } from '../src/drug/rxnorm.js';
import { CatalogDataProvider } from '../src/drug/catalog.js';

/**
 * Integration-level coverage for VerifyOptions.evidence (the seam that's
 * actually LIVE in production — see src/cli.ts, which constructs
 * RxNormDataProvider/CatalogDataProvider once at module scope and passes
 * them through here). tests/cli.test.ts/cli-serve.test.ts exercise the
 * real subprocess but fail under this sandbox (tsx IPC EPERM — a known,
 * pre-existing, unrelated sandbox limitation); verify() is the highest
 * seam below that which IS testable here, and it's the exact function
 * src/cli.ts's runVerify calls with exactly this evidence plumbing.
 */

const provider: RxNormProvider = {
  getConcept: (ndcOrName) =>
    ndcOrName.toLowerCase().includes('lisinopril')
      ? { rxcui: 'FX-LISIN-10', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet', name: 'Lisinopril 10mg tablet' }
      : null
};

const rxnormProvider: RxNormEquivalenceProvider = {
  getByNdc: (ndc11) =>
    ndc11 === '55555555555'
      ? {
          rxcui: '900001',
          tty: 'SCD',
          displayName: 'lisinopril 10 MG Oral Tablet',
          ingredient: 'lisinopril',
          strength: '10mg',
          doseForm: 'tablet'
        }
      : null
};

// Source brand name is unknown to `provider`, and its NDC isn't in
// FixtureProvider/provider's table either -- so the pre-existing ladder
// can't resolve it and, since the entered name DOES resolve (to a
// different-looking ingredient token set), lands on unknown_drug yellow.
const source = { patientName: 'Test Patient', drug: { name: 'Zestrabrand 10 Mg Tablet', ndc: '55555-5555-55' } };
const entered = { patientName: 'Test Patient', drug: { name: 'Lisinopril 10 Mg Tablet' } };

function drugVerdict(result: ReturnType<typeof verify>) {
  const v = result.verdicts.find((v) => v.field === 'drug');
  if (!v) throw new Error('drug verdict missing');
  return v;
}

describe('verify() + VerifyOptions.evidence (the live production seam — see src/cli.ts)', () => {
  it('is yellow (unknown_drug) with no evidence supplied at all', () => {
    const result = verify(source, entered, provider);
    const drug = drugVerdict(result);
    expect(drug.status).toBe('yellow');
    expect(drug.reasonCode).toBe('unknown_drug');
  });

  it('upgrades to green when synthetic RxNorm evidence is supplied via VerifyOptions.evidence', () => {
    const result = verify(source, entered, provider, { evidence: { rxnormProvider } });
    const drug = drugVerdict(result);
    expect(drug.status).toBe('green');
    expect(drug.reasonCode).toBe('rxnorm_scd_match');
    // Requirement: the explanation must surface WHICH layer upgraded it
    // and name the concept, not just say "green" -- the pharmacist needs
    // to see why.
    expect(drug.explanation).toContain('RxNorm');
    expect(drug.explanation).toContain('lisinopril 10 MG Oral Tablet');
  });

  it('stays yellow when evidence is explicitly supplied but empty ({})', () => {
    const result = verify(source, entered, provider, { evidence: {} });
    expect(drugVerdict(result).status).toBe('yellow');
  });

  it('GRACEFUL ABSENCE: stays yellow, behaves identically to no evidence, when the real provider classes are backed by nonexistent data files', () => {
    // Mirrors EXACTLY how src/cli.ts constructs these at module scope —
    // the only difference here is pointing them at a path that can't
    // exist, to prove a missing data/*.json.gz never changes verify()'s
    // output (see RxNormDataProvider/CatalogDataProvider's "GRACEFUL
    // ABSENCE" doc).
    const absentRxnorm = new RxNormDataProvider('/nonexistent/rxnorm-data.json.gz');
    const absentCatalog = new CatalogDataProvider('/nonexistent/catalog-data.json.gz');

    const withoutEvidence = verify(source, entered, provider);
    const withAbsentEvidence = verify(source, entered, provider, {
      evidence: { rxnormProvider: absentRxnorm, catalogProvider: absentCatalog }
    });

    expect(withAbsentEvidence).toEqual(withoutEvidence);
    expect(drugVerdict(withAbsentEvidence).status).toBe('yellow');
  });

  it('evidence is never consulted when skipDrugLookup is true (drug field stays pending, not upgraded)', () => {
    const result = verify(source, entered, provider, { skipDrugLookup: true, evidence: { rxnormProvider } });
    const drug = drugVerdict(result);
    expect(drug.reasonCode).toBe('pending_lookup');
    expect(drug.status).toBe('yellow');
  });

  it('a genuine RED verdict (strength contradiction) is never reconsidered even with matching evidence', () => {
    const conflictingSource = { drug: { name: 'Lisinopril 10 Mg Tablet', ndc: '55555-5555-55' } };
    const conflictingEntered = { drug: { name: 'Lisinopril 20 Mg Tablet' } };
    const result = verify(conflictingSource, conflictingEntered, provider, { evidence: { rxnormProvider } });
    const drug = drugVerdict(result);
    expect(drug.status).toBe('red');
    expect(drug.reasonCode).toBe('drug_mismatch');
  });
});
