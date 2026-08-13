import { describe, it, expect } from 'vitest';
import {
  compareDrugs,
  type RxNormProvider,
  type RxNormEquivalenceProvider,
  type CatalogEquivalenceProvider,
  type DrugEquivalenceEvidence
} from '../src/drug/index.js';
import { RxNormDataProvider } from '../src/drug/rxnorm.js';
import { CatalogDataProvider } from '../src/drug/catalog.js';

/**
 * Integration tests for compareDrugs' 4th `evidence` argument (see
 * src/drug/index.ts's tryEquivalenceUpgrade / DrugEquivalenceEvidence) —
 * the RxNorm (public) and wholesaler-catalog (internal) equivalence
 * layers this branch adds. These use lightweight, hand-written mock
 * providers satisfying RxNormEquivalenceProvider/CatalogEquivalenceProvider
 * directly (rather than the real RxNormDataProvider/CatalogDataProvider
 * classes, which already have their own dedicated fixture-file-backed
 * unit tests in tests/rxnorm.test.ts / tests/catalog.test.ts) so each
 * scenario's evidence can be pinned exactly, deterministically.
 *
 * All drug names are the SYNTHETIC vocabulary already used elsewhere in
 * this suite, plus real pharmacological pairs (metoprolol tartrate vs
 * succinate) used ONLY as generic drug-identity terms — never real
 * patient/prescriber data, consistent with this repo's README.
 */

const NULL_PROVIDER: RxNormProvider = { getConcept: () => null };

describe('compareDrugs + rxnorm evidence: rxnorm_scd_match', () => {
  it('upgrades an unknown_drug yellow to green when the written NDC (via RxNorm) and entered name (via the base provider) agree on ingredient/strength/dose form', () => {
    const provider: RxNormProvider = {
      getConcept: (ndcOrName) =>
        ndcOrName.toLowerCase().includes('lisinopril')
          ? { rxcui: 'FX-LISIN-10', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet', name: 'Lisinopril 10mg tablet' }
          : null
    };
    const rxnormProvider: RxNormEquivalenceProvider = {
      getByNdc: (ndc11) =>
        ndc11 === '55555555555'
          ? { rxcui: '900001', tty: 'SCD', displayName: 'lisinopril 10 MG Oral Tablet', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet' }
          : null
    };

    // Without evidence: source's brand name doesn't resolve at all (this
    // provider only knows "lisinopril") and the entered name DOES -- so
    // the pre-existing ladder can't even reach the component-fallback
    // (ingredient sets differ: "zestrabrand" vs "lisinopril") and lands
    // on unknown_drug yellow.
    const src = { name: 'Zestrabrand 10 Mg Tablet', ndc: '55555-5555-55' };
    const ent = { name: 'Lisinopril 10 Mg Tablet' };

    const withoutEvidence = compareDrugs(src, ent, provider);
    expect(withoutEvidence.status).toBe('yellow');
    expect(withoutEvidence.reasonCode).toBe('unknown_drug');

    const withEvidence = compareDrugs(src, ent, provider, { rxnormProvider });
    expect(withEvidence.status).toBe('green');
    expect(withEvidence.reasonCode).toBe('rxnorm_scd_match');
    expect(withEvidence.explanation).toContain('RxNorm');
  });

  it('does NOT upgrade when the RxNorm concept and the entered concept genuinely disagree (falls through to the original yellow)', () => {
    const provider: RxNormProvider = {
      getConcept: (ndcOrName) =>
        ndcOrName.toLowerCase().includes('metformin')
          ? { rxcui: 'FX-MET-500', ingredient: 'metformin', strength: '500mg', doseForm: 'tablet', name: 'Metformin 500mg tablet' }
          : null
    };
    const rxnormProvider: RxNormEquivalenceProvider = {
      // Written NDC resolves to a DIFFERENT drug than what was entered.
      getByNdc: (ndc11) =>
        ndc11 === '66666666666'
          ? { rxcui: '900002', tty: 'SCD', displayName: 'lisinopril 10 MG Oral Tablet', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet' }
          : null
    };
    // Same stated strength on both sides (so the pre-existing
    // strength-contradiction RED check doesn't fire first) but a
    // genuinely different drug name -- isolates the concept-resolution
    // yellow path this test targets.
    const src = { name: 'Unknownbrand 500 Mg Tablet', ndc: '66666-6666-66' };
    const ent = { name: 'Metformin 500 Mg Tablet' };

    const result = compareDrugs(src, ent, provider, { rxnormProvider });
    expect(result.status).toBe('yellow');
    expect(result.reasonCode).toBe('unknown_drug');
  });
});

describe('compareDrugs + catalog evidence: catalog_gcn_match / catalog_gcn_mismatch', () => {
  it('upgrades to green when written NDC and entered name resolve to the SAME catalog GCN (same-GCN, different-labeler generics)', () => {
    const catalogProvider: CatalogEquivalenceProvider = {
      getByNdc: (ndc11) => (ndc11 === '77777777777' ? { gcn: 'GCN-500' } : null),
      resolveGcnByName: (name) => (name.toLowerCase().includes('bimoxatin') ? 'GCN-500' : null)
    };
    const src = { name: 'Bimolabs 25 Mg Capsule', ndc: '77777-7777-77' };
    const ent = { name: 'Bimoxatin 25 Mg Capsule' };

    const withoutEvidence = compareDrugs(src, ent, NULL_PROVIDER);
    expect(withoutEvidence.status).toBe('yellow');

    const result = compareDrugs(src, ent, NULL_PROVIDER, { catalogProvider });
    expect(result.status).toBe('green');
    expect(result.reasonCode).toBe('catalog_gcn_match');
    expect(result.explanation).toContain('GCN-500');
  });

  it('relabels (but never greens or reds) a genuine GCN mismatch as catalog_gcn_mismatch', () => {
    const catalogProvider: CatalogEquivalenceProvider = {
      getByNdc: (ndc11) => (ndc11 === '88888888888' ? { gcn: 'GCN-100' } : null),
      resolveGcnByName: (name) => (name.toLowerCase().includes('bimoxatin') ? 'GCN-200' : null)
    };
    const src = { name: 'Somebrand 25 Mg Capsule', ndc: '88888-8888-88' };
    const ent = { name: 'Bimoxatin 25 Mg Capsule' };

    const result = compareDrugs(src, ent, NULL_PROVIDER, { catalogProvider });
    expect(result.status).toBe('yellow');
    expect(result.reasonCode).toBe('catalog_gcn_mismatch');
  });

  it('a written NDC with no catalog entry (or no GCN) -> no verdict change', () => {
    const catalogProvider: CatalogEquivalenceProvider = {
      getByNdc: () => null,
      resolveGcnByName: () => 'GCN-999'
    };
    const src = { name: 'Somebrand 25 Mg Capsule', ndc: '99999-9999-99' };
    const ent = { name: 'Bimoxatin 25 Mg Capsule' };
    const baseline = compareDrugs(src, ent, NULL_PROVIDER);
    const result = compareDrugs(src, ent, NULL_PROVIDER, { catalogProvider });
    expect(result).toEqual(baseline);
  });

  it('an AMBIGUOUS catalog resolution (entered side cannot resolve to one GCN) -> no verdict change, never a guess', () => {
    const catalogProvider: CatalogEquivalenceProvider = {
      getByNdc: (ndc11) => (ndc11 === '10101010101' ? { gcn: 'GCN-300' } : null),
      resolveGcnByName: () => null // simulates resolveGcnByName's own ambiguity -> null contract
    };
    const src = { name: 'Somebrand 25 Mg Capsule', ndc: '10101-0101-01' };
    const ent = { name: 'Bimoxatin 25 Mg Capsule' };
    const baseline = compareDrugs(src, ent, NULL_PROVIDER);
    const result = compareDrugs(src, ent, NULL_PROVIDER, { catalogProvider });
    expect(result).toEqual(baseline);
    expect(result.status).toBe('yellow');
  });
});

describe('IRON RULE: metoprolol tartrate vs succinate must stay non-green through BOTH new layers', () => {
  const provider: RxNormProvider = {
    getConcept: (ndcOrName) => {
      const n = ndcOrName.toLowerCase();
      if (n.includes('succinate')) {
        return { rxcui: 'MET-SUCC', ingredient: 'metoprolol succinate', strength: '50mg', doseForm: 'tablet', name: 'Metoprolol Succinate 50mg tablet' };
      }
      if (n.includes('tartrate')) {
        return { rxcui: 'MET-TART', ingredient: 'metoprolol tartrate', strength: '50mg', doseForm: 'tablet', name: 'Metoprolol Tartrate 50mg tablet' };
      }
      return null;
    }
  };
  const src = { name: 'Metoprolol Tartrate 50 Mg Tablet', ndc: '20202-0202-02' };
  const ent = { name: 'Metoprolol Succinate 50 Mg Tablet' };

  it('the pre-existing ladder alone is yellow, not green (sanity baseline)', () => {
    const baseline = compareDrugs(src, ent, provider);
    expect(baseline.status).toBe('yellow');
  });

  it('RxNorm evidence stating the WRITTEN (tartrate) identity never greens against the succinate entered name', () => {
    const rxnormProvider: RxNormEquivalenceProvider = {
      getByNdc: (ndc11) =>
        ndc11 === '20202020202'
          ? { rxcui: '900010', tty: 'SCD', displayName: 'metoprolol tartrate 50 MG Oral Tablet', ingredient: 'metoprolol tartrate', strength: '50mg', doseForm: 'tablet' }
          : null
    };
    const result = compareDrugs(src, ent, provider, { rxnormProvider });
    expect(result.status).not.toBe('green');
  });

  it('catalog evidence with genuinely different GCNs for tartrate vs succinate never greens', () => {
    const catalogProvider: CatalogEquivalenceProvider = {
      getByNdc: (ndc11) => (ndc11 === '20202020202' ? { gcn: 'GCN-TART' } : null),
      resolveGcnByName: (name) => (name.toLowerCase().includes('succinate') ? 'GCN-SUCC' : null)
    };
    const result = compareDrugs(src, ent, provider, { catalogProvider });
    expect(result.status).not.toBe('green');
  });

  it('BOTH layers supplied together, both correctly reflecting the real distinction, never green', () => {
    const rxnormProvider: RxNormEquivalenceProvider = {
      getByNdc: (ndc11) =>
        ndc11 === '20202020202'
          ? { rxcui: '900010', tty: 'SCD', displayName: 'metoprolol tartrate 50 MG Oral Tablet', ingredient: 'metoprolol tartrate', strength: '50mg', doseForm: 'tablet' }
          : null
    };
    const catalogProvider: CatalogEquivalenceProvider = {
      getByNdc: (ndc11) => (ndc11 === '20202020202' ? { gcn: 'GCN-TART' } : null),
      resolveGcnByName: (name) => (name.toLowerCase().includes('succinate') ? 'GCN-SUCC' : null)
    };
    const result = compareDrugs(src, ent, provider, { rxnormProvider, catalogProvider });
    expect(result.status).not.toBe('green');
  });
});

describe('a genuine strength contradiction stays RED even with evidence that would otherwise say green', () => {
  it('never reconsiders a RED verdict, no matter what the evidence providers say', () => {
    // Both names state a DIFFERENT strength for the same-looking drug --
    // this fires compareDrugsCore's strength-contradiction RED check
    // long before any concept/evidence resolution runs.
    const src = { name: 'Lisinopril 10 Mg Tablet', ndc: '30303-0303-03' };
    const ent = { name: 'Lisinopril 20 Mg Tablet' };

    const rxnormProvider: RxNormEquivalenceProvider = {
      // Deliberately "confirms" a false equivalence -- must never be consulted.
      getByNdc: () => ({ rxcui: 'x', tty: 'SCD', displayName: 'x', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet' })
    };
    const catalogProvider: CatalogEquivalenceProvider = {
      getByNdc: () => ({ gcn: 'GCN-1' }),
      resolveGcnByName: () => 'GCN-1'
    };

    const baseline = compareDrugs(src, ent, NULL_PROVIDER);
    expect(baseline.status).toBe('red');
    expect(baseline.reasonCode).toBe('drug_mismatch');

    const withEvidence = compareDrugs(src, ent, NULL_PROVIDER, { rxnormProvider, catalogProvider });
    expect(withEvidence).toEqual(baseline);
  });
});

describe('graceful absence: real provider classes pointed at missing data files never change engine behavior', () => {
  it('RxNormDataProvider + CatalogDataProvider backed by nonexistent files behave identically to omitting evidence entirely', () => {
    const rxnormProvider = new RxNormDataProvider('/nonexistent/path/rxnorm-data.json.gz');
    const catalogProvider = new CatalogDataProvider('/nonexistent/path/catalog-data.json.gz');
    const src = { name: 'Zestrabrand 10 Mg Tablet', ndc: '40404-0404-04' };
    const ent = { name: 'Lisinopril 10 Mg Tablet' };
    const provider: RxNormProvider = {
      getConcept: (n) => (n.toLowerCase().includes('lisinopril') ? { rxcui: 'x', ingredient: 'lisinopril', strength: '10mg', doseForm: 'tablet', name: 'x' } : null)
    };

    const baseline = compareDrugs(src, ent, provider);
    const withGracefullyAbsentEvidence = compareDrugs(src, ent, provider, { rxnormProvider, catalogProvider });
    expect(withGracefullyAbsentEvidence).toEqual(baseline);
    expect(withGracefullyAbsentEvidence.status).toBe('yellow');
  });
});

describe('evidence is never consulted for a GREEN pre-existing verdict', () => {
  it('an exact NDC match stays exact_match, untouched by evidence', () => {
    const src = { ndc: '00071-0155-23' };
    const ent = { ndc: '00071-0155-23' };
    const rxnormProvider: RxNormEquivalenceProvider = { getByNdc: () => null };
    const result = compareDrugs(src, ent, NULL_PROVIDER, { rxnormProvider });
    expect(result.status).toBe('green');
    expect(result.reasonCode).toBe('exact_match');
  });
});

describe('DrugEquivalenceEvidence type is a plain optional bag (no evidence = old behavior byte for byte)', () => {
  it('omitting the 4th argument entirely is identical to passing undefined', () => {
    const src = { name: 'Lisinopril 10 Mg Tablet', ndc: '50505-0505-05' };
    const ent = { name: 'Lisinopril 10 Mg Tablet' };
    const a = compareDrugs(src, ent, NULL_PROVIDER);
    const b = compareDrugs(src, ent, NULL_PROVIDER, undefined);
    const c: DrugEquivalenceEvidence = {};
    const d = compareDrugs(src, ent, NULL_PROVIDER, c);
    expect(a).toEqual(b);
    expect(a).toEqual(d);
  });
});
