import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { RxNormDataProvider, doseFormsEquivalent, rxNormMatchesLocalConcept } from '../src/drug/rxnorm.js';
import type { RxNormConcept, RxNormData } from '../src/drug/rxnorm-data-format.js';

// Fully SYNTHETIC RxNorm-shaped fixture (invented RXCUIs/NDCs — never
// real RxNorm content, unlike the actual bundled data/rxnorm-data.json.gz
// which IS built from real public NLM data). Mirrors
// tests/local-name-resolution.test.ts's fixture-file pattern.
const CONCEPTS: RxNormConcept[] = [
  {
    rxcui: '900001',
    tty: 'SCD',
    displayName: 'zylodrine 10 MG Oral Tablet',
    ingredient: 'zylodrine',
    strength: '10mg',
    doseForm: 'oral tablet'
  },
  {
    rxcui: '900002',
    tty: 'SBD',
    scdRxcui: '900001',
    displayName: 'zylodrine 10 MG Oral Tablet [Zylexa]',
    ingredient: 'zylodrine',
    strength: '10mg',
    doseForm: 'oral tablet'
  },
  {
    rxcui: '900003',
    tty: 'SCD',
    displayName: '24 HR bimoxatin 30 MG Extended Release Oral Tablet',
    ingredient: 'bimoxatin',
    strength: '30mg',
    doseForm: 'extended release oral tablet'
  }
];

const DATA: RxNormData = {
  generatedAt: '2026-01-01T00:00:00.000Z',
  source: 'synthetic-fixture',
  attribution: 'synthetic-fixture',
  concepts: CONCEPTS,
  ndcIndex: {
    '11111000101': 0, // zylodrine generic
    '22222000201': 1 // Zylexa brand
  },
  scdDisplayNames: {
    '900001': 'zylodrine 10 MG Oral Tablet'
  }
};

let dataPath: string;
let workDir: string;
let provider: RxNormDataProvider;

beforeAll(() => {
  workDir = mkdtempSync(path.join(tmpdir(), 'rx-verify-rxnorm-fixture-'));
  dataPath = path.join(workDir, 'fixture.json.gz');
  writeFileSync(dataPath, gzipSync(Buffer.from(JSON.stringify(DATA), 'utf8')));
  provider = new RxNormDataProvider(dataPath);
});

afterAll(() => {
  rmSync(workDir, { recursive: true, force: true });
});

describe('RxNormDataProvider', () => {
  it('resolves a known NDC to its concept', () => {
    const c = provider.getByNdc('11111000101');
    expect(c?.ingredient).toBe('zylodrine');
    expect(c?.tty).toBe('SCD');
  });

  it('resolves a branded NDC and carries its scdRxcui', () => {
    const c = provider.getByNdc('22222000201');
    expect(c?.tty).toBe('SBD');
    expect(c?.scdRxcui).toBe('900001');
  });

  it('returns null for an unknown NDC', () => {
    expect(provider.getByNdc('00000000000')).toBeNull();
  });

  it('getScdDisplayName resolves a known scdRxcui, including one with no NDC of its own', () => {
    expect(provider.getScdDisplayName('900001')).toBe('zylodrine 10 MG Oral Tablet');
  });

  it('getScdDisplayName returns null for an unknown rxcui', () => {
    expect(provider.getScdDisplayName('000000')).toBeNull();
  });

  it('GRACEFUL ABSENCE: a missing data file never throws, every lookup just misses', () => {
    const missing = new RxNormDataProvider(path.join(workDir, 'does-not-exist.json.gz'));
    expect(() => missing.getByNdc('11111000101')).not.toThrow();
    expect(missing.getByNdc('11111000101')).toBeNull();
    expect(missing.getScdDisplayName('900001')).toBeNull();
  });

  it('GRACEFUL ABSENCE: a corrupt/unreadable file also degrades to empty, never throws', () => {
    const corruptPath = path.join(workDir, 'corrupt.json.gz');
    writeFileSync(corruptPath, Buffer.from('not a gzip file'));
    const corrupt = new RxNormDataProvider(corruptPath);
    expect(corrupt.getByNdc('11111000101')).toBeNull();
  });
});

describe('doseFormsEquivalent', () => {
  it('matches RxNorm "oral tablet" against openFDA "tablet" (route word ignored)', () => {
    expect(doseFormsEquivalent('oral tablet', 'tablet')).toBe(true);
  });

  it('matches RxNorm "extended release oral tablet" against openFDA "tablet, extended release" (word order + route)', () => {
    expect(doseFormsEquivalent('extended release oral tablet', 'tablet, extended release')).toBe(true);
  });

  it('does NOT match plain tablet against extended-release tablet', () => {
    expect(doseFormsEquivalent('oral tablet', 'tablet, extended release')).toBe(false);
  });

  it('does NOT match genuinely different forms (capsule vs tablet)', () => {
    expect(doseFormsEquivalent('oral capsule', 'tablet')).toBe(false);
  });

  it('never matches when either side is empty', () => {
    expect(doseFormsEquivalent('', 'tablet')).toBe(false);
    expect(doseFormsEquivalent('oral tablet', '')).toBe(false);
  });
});

describe('rxNormMatchesLocalConcept', () => {
  const rxEntry = { ingredient: 'zylodrine', strength: '10mg', doseForm: 'oral tablet', displayName: 'x' };

  it('matches when ingredient/strength agree and dose forms are equivalent', () => {
    expect(rxNormMatchesLocalConcept(rxEntry, { ingredient: 'zylodrine', strength: '10mg', doseForm: 'tablet' })).toBe(
      true
    );
  });

  it('does not match on a different strength', () => {
    expect(
      rxNormMatchesLocalConcept(rxEntry, { ingredient: 'zylodrine', strength: '20mg', doseForm: 'tablet' })
    ).toBe(false);
  });

  it('does not match on a different ingredient', () => {
    expect(
      rxNormMatchesLocalConcept(rxEntry, { ingredient: 'bimoxatin', strength: '10mg', doseForm: 'tablet' })
    ).toBe(false);
  });

  it('does not match on a genuinely different dose form', () => {
    expect(
      rxNormMatchesLocalConcept(rxEntry, { ingredient: 'zylodrine', strength: '10mg', doseForm: 'capsule' })
    ).toBe(false);
  });

  it('never matches on empty fields (defense in depth)', () => {
    expect(rxNormMatchesLocalConcept(rxEntry, { ingredient: '', strength: '10mg', doseForm: 'tablet' })).toBe(false);
  });
});
