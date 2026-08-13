import { describe, it, expect } from 'vitest';
import {
  findLatestPrescribeZipUrl,
  parseConsoLine,
  parseRelLine,
  parseSatNdcLine,
  extractDoseFormVocabulary,
  splitIngredientStrength,
  buildRxNormConcept,
  deriveScdRxcuiMap,
  buildRxNormDataset,
  type ConsoRow,
  type RelRow,
  type SatNdcRow
} from '../scripts/build-rxnorm-data.js';

// Tiny SYNTHETIC RRF-shaped fixture. Row SHAPES mirror real RXNCONSO/
// RXNSAT/RXNREL rows (see this repo's recon of a real 2026-08 release —
// confirmed field positions), but RXCUIs/NDCs are invented (900xxx /
// 111xxxxxxxx), never the real bundled data/rxnorm-data.json.gz content.
// Ingredient/drug-name vocabulary is the same synthetic set used
// elsewhere in this suite (zylodrine, bimoxatin) plus a metoprolol pair
// specifically to lock down the tartrate/succinate distinction — used
// here only as a real, well-known pharmacological name, not any kind of
// patient/prescriber data.
function conso(rxcui: string, sab: string, tty: string, str: string): string {
  // Field positions: RXCUI(0)...SAB(11)|TTY(12)|CODE(13)|STR(14)|...
  const f = new Array(18).fill('');
  f[0] = rxcui;
  f[11] = sab;
  f[12] = tty;
  f[13] = rxcui;
  f[14] = str;
  return f.join('|');
}
function rel(rxcui1: string, rela: string, rxcui2: string): string {
  const f = new Array(16).fill('');
  f[0] = rxcui1;
  f[4] = rxcui2;
  f[7] = rela;
  f[10] = 'RXNORM';
  return f.join('|');
}
function sat(rxcui: string, atn: string, sab: string, atv: string): string {
  const f = new Array(13).fill('');
  f[0] = rxcui;
  f[8] = atn;
  f[9] = sab;
  f[10] = atv;
  return f.join('|');
}

describe('findLatestPrescribeZipUrl', () => {
  it('picks the most recent RxNorm_full_prescribe_MMDDYYYY.zip link by date, not list order', () => {
    const html = `
      <a href="https://download.nlm.nih.gov/rxnorm/RxNorm_full_prescribe_01062025.zip">old</a>
      <a href="https://download.nlm.nih.gov/rxnorm/RxNorm_full_prescribe_08032026.zip">newest</a>
      <a href="https://download.nlm.nih.gov/rxnorm/RxNorm_full_prescribe_02052024.zip">oldest</a>
    `;
    const { url, releaseId } = findLatestPrescribeZipUrl(html);
    expect(url).toBe('https://download.nlm.nih.gov/rxnorm/RxNorm_full_prescribe_08032026.zip');
    expect(releaseId).toBe('RxNorm_full_prescribe_08032026');
  });

  it('throws (never silently picks nothing) when no matching link is present', () => {
    expect(() => findLatestPrescribeZipUrl('<html>no links here</html>')).toThrow();
  });
});

describe('parseConsoLine / parseRelLine / parseSatNdcLine', () => {
  it('parses a well-formed RXNCONSO line', () => {
    const row = parseConsoLine(conso('900001', 'RXNORM', 'SCD', 'zylodrine 10 MG Oral Tablet'));
    expect(row).toEqual({ rxcui: '900001', sab: 'RXNORM', tty: 'SCD', str: 'zylodrine 10 MG Oral Tablet' });
  });

  it('returns null for a blank line', () => {
    expect(parseConsoLine('')).toBeNull();
  });

  it('parses a well-formed RXNREL has_tradename line', () => {
    expect(parseRelLine(rel('900002', 'has_tradename', '900001'))).toEqual({
      rxcui1: '900002',
      rela: 'has_tradename',
      rxcui2: '900001'
    });
  });

  it('parses an ATN=NDC RXNSAT line', () => {
    expect(parseSatNdcLine(sat('900001', 'NDC', 'RXNORM', '11111000101'))).toEqual({
      rxcui: '900001',
      ndcRaw: '11111000101'
    });
  });

  it('returns null for a non-NDC RXNSAT attribute row', () => {
    expect(parseSatNdcLine(sat('900001', 'RXN_BN_CARDINALITY', 'RXNORM', 'single'))).toBeNull();
  });
});

describe('extractDoseFormVocabulary', () => {
  it('collects distinct SAB=RXNORM TTY=DF strings, ignoring other TTYs/SABs', () => {
    const rows: ConsoRow[] = [
      { rxcui: '1', sab: 'RXNORM', tty: 'DF', str: 'Oral Tablet' },
      { rxcui: '2', sab: 'RXNORM', tty: 'DF', str: 'Extended Release Oral Tablet' },
      { rxcui: '3', sab: 'RXNORM', tty: 'DF', str: 'Oral Tablet' }, // duplicate
      { rxcui: '4', sab: 'MTHSPL', tty: 'DF', str: 'Should Be Ignored (wrong SAB)' },
      { rxcui: '5', sab: 'RXNORM', tty: 'SCD', str: 'Should Be Ignored (wrong TTY)' }
    ];
    const vocab = extractDoseFormVocabulary(rows);
    expect(vocab).toEqual(['Extended Release Oral Tablet', 'Oral Tablet']); // sorted by descending length
  });
});

describe('splitIngredientStrength', () => {
  it('splits a plain single-ingredient segment', () => {
    expect(splitIngredientStrength('zylodrine 10 MG')).toEqual({ ingredient: 'zylodrine', strength: '10 MG' });
  });

  it('splits a combo segment (no dose form attached, as in a non-final " / " component)', () => {
    expect(splitIngredientStrength('hydrochlorothiazide 50 MG')).toEqual({
      ingredient: 'hydrochlorothiazide',
      strength: '50 MG'
    });
  });

  it('handles a compound concentration strength', () => {
    expect(splitIngredientStrength('zylodrine 0.25 MG/ML')).toEqual({
      ingredient: 'zylodrine',
      strength: '0.25 MG/ML'
    });
  });

  it('strips a leading volume descriptor ("2 ML ...") before finding the real strength', () => {
    expect(splitIngredientStrength('2 ML zylodrine 0.25 MG/ML')).toEqual({
      ingredient: 'zylodrine',
      strength: '0.25 MG/ML'
    });
  });

  it('strips a leading duration descriptor ("24 HR ...")', () => {
    expect(splitIngredientStrength('24 HR bimoxatin 30 MG')).toEqual({ ingredient: 'bimoxatin', strength: '30 MG' });
  });

  it('returns null (never a guess) when there is no digit at all', () => {
    expect(splitIngredientStrength('zylodrine tablet')).toBeNull();
  });

  it('returns null when the segment is entirely a digit-led token with nothing else', () => {
    expect(splitIngredientStrength('10')).toBeNull();
  });
});

describe('buildRxNormConcept', () => {
  const doseFormVocab = extractDoseFormVocabulary([
    { rxcui: 'x', sab: 'RXNORM', tty: 'DF', str: 'Oral Tablet' },
    { rxcui: 'x', sab: 'RXNORM', tty: 'DF', str: 'Extended Release Oral Tablet' }
  ]);

  it('builds a single-ingredient SCD concept', () => {
    const concept = buildRxNormConcept(
      { rxcui: '900001', sab: 'RXNORM', tty: 'SCD', str: 'zylodrine 10 MG Oral Tablet' },
      doseFormVocab
    );
    expect(concept).toEqual({
      rxcui: '900001',
      tty: 'SCD',
      displayName: 'zylodrine 10 MG Oral Tablet',
      ingredient: 'zylodrine',
      strength: '10mg',
      doseForm: 'oral tablet'
    });
  });

  it('builds an SBD concept, stripping the trailing [Brand] bracket before parsing', () => {
    const concept = buildRxNormConcept(
      { rxcui: '900002', sab: 'RXNORM', tty: 'SBD', str: 'zylodrine 10 MG Oral Tablet [Zylexa]' },
      doseFormVocab
    );
    expect(concept?.displayName).toBe('zylodrine 10 MG Oral Tablet [Zylexa]');
    expect(concept?.ingredient).toBe('zylodrine');
    expect(concept?.doseForm).toBe('oral tablet');
  });

  it('joins and alphabetizes a combo product’s ingredients/strengths together, matching buildConcept’s openFDA convention', () => {
    const concept = buildRxNormConcept(
      { rxcui: '900003', sab: 'RXNORM', tty: 'SCD', str: 'zylodrine 50 MG / bimoxatin 25 MG Oral Tablet' },
      doseFormVocab
    );
    expect(concept?.ingredient).toBe('bimoxatin;zylodrine');
    expect(concept?.strength).toBe('25mg;50mg');
  });

  it('picks the LONGEST matching dose form (extended release variant), not a shorter accidental suffix', () => {
    const concept = buildRxNormConcept(
      { rxcui: '900004', sab: 'RXNORM', tty: 'SCD', str: '24 HR bimoxatin 30 MG Extended Release Oral Tablet' },
      doseFormVocab
    );
    expect(concept?.doseForm).toBe('extended release oral tablet');
    expect(concept?.ingredient).toBe('bimoxatin');
    expect(concept?.strength).toBe('30mg');
  });

  it('skips (never guesses) a TTY it does not handle (GPCK)', () => {
    expect(
      buildRxNormConcept(
        { rxcui: '900005', sab: 'RXNORM', tty: 'GPCK', str: '{28 (zylodrine 10 MG Oral Tablet)} Pack' },
        doseFormVocab
      )
    ).toBeNull();
  });

  it('skips a row from a non-RXNORM SAB', () => {
    expect(
      buildRxNormConcept({ rxcui: '900006', sab: 'MTHSPL', tty: 'SCD', str: 'zylodrine 10 MG Oral Tablet' }, doseFormVocab)
    ).toBeNull();
  });

  it('skips (never guesses) a string that does not end in any known dose form', () => {
    expect(
      buildRxNormConcept({ rxcui: '900007', sab: 'RXNORM', tty: 'SCD', str: 'zylodrine 10 MG Unknown Form' }, doseFormVocab)
    ).toBeNull();
  });

  it('skips (never guesses) a digit-led ingredient name it cannot cleanly split (documented limitation)', () => {
    expect(
      buildRxNormConcept({ rxcui: '900008', sab: 'RXNORM', tty: 'SCD', str: '6-zylodrinic acid 500 MG Oral Tablet' }, doseFormVocab)
    ).toBeNull();
  });
});

describe('deriveScdRxcuiMap', () => {
  it('links an SBD to its SCD ONLY via a confirmed has_tradename relationship into the known SCD set', () => {
    const relRows: RelRow[] = [
      { rxcui1: '900002', rela: 'has_tradename', rxcui2: '900001' }, // SBD -> SCD (correct direction, per real-release recon)
      { rxcui1: '900002', rela: 'isa', rxcui2: '900099' }, // different RELA -- ignored
      { rxcui1: '900003', rela: 'has_tradename', rxcui2: '900098' } // target NOT a known SCD -- ignored
    ];
    const map = deriveScdRxcuiMap(relRows, new Set(['900002', '900003']), new Set(['900001']));
    expect(map.get('900002')).toBe('900001');
    expect(map.has('900003')).toBe(false);
  });

  it('first relationship found per SBD wins', () => {
    const relRows: RelRow[] = [
      { rxcui1: '900002', rela: 'has_tradename', rxcui2: '900001' },
      { rxcui1: '900002', rela: 'has_tradename', rxcui2: '900050' }
    ];
    const map = deriveScdRxcuiMap(relRows, new Set(['900002']), new Set(['900001', '900050']));
    expect(map.get('900002')).toBe('900001');
  });
});

describe('buildRxNormDataset (pure end-to-end transform)', () => {
  const consoRows: ConsoRow[] = [
    { rxcui: '1', sab: 'RXNORM', tty: 'DF', str: 'Oral Tablet' },
    { rxcui: '1', sab: 'RXNORM', tty: 'DF', str: 'Oral Capsule' },
    { rxcui: '900001', sab: 'RXNORM', tty: 'SCD', str: 'zylodrine 10 MG Oral Tablet' },
    { rxcui: '900002', sab: 'RXNORM', tty: 'SBD', str: 'zylodrine 10 MG Oral Tablet [Zylexa]' },
    // An SCD with NO NDC of its own -- should still appear in
    // scdDisplayNames but NOT in concepts/ndcIndex.
    { rxcui: '900003', sab: 'RXNORM', tty: 'SCD', str: 'bimoxatin 25 MG Oral Capsule' },
    // ignored: not SAB=RXNORM
    { rxcui: '900004', sab: 'MTHSPL', tty: 'SCD', str: 'ignored 1 MG Oral Tablet' }
  ];
  const ndcRows: SatNdcRow[] = [
    { rxcui: '900001', ndcRaw: '11111-0001-01' },
    { rxcui: '900002', ndcRaw: '22222-0002-01' },
    { rxcui: '900099', ndcRaw: '99999-9999-99' } // rxcui with no built concept -- must be dropped
  ];
  const relRows: RelRow[] = [{ rxcui1: '900002', rela: 'has_tradename', rxcui2: '900001' }];

  const { data, stats } = buildRxNormDataset({
    consoRows,
    ndcRows,
    relRows,
    generatedAt: '2026-01-01T00:00:00.000Z',
    source: 'fixture'
  });

  it('builds exactly the concepts that have a matched NDC', () => {
    expect(stats.scdCount).toBe(1);
    expect(stats.sbdCount).toBe(1);
    expect(stats.ndcCount).toBe(2);
  });

  it('links the SBD to its SCD via RXNREL', () => {
    const sbdIdx = data.ndcIndex['22222000201'] as number;
    expect(data.concepts[sbdIdx]?.scdRxcui).toBe('900001');
  });

  it('includes an SCD with no NDC of its own in scdDisplayNames, but not in ndcIndex', () => {
    expect(data.scdDisplayNames['900003']).toBe('bimoxatin 25 MG Oral Capsule');
    expect(Object.values(data.ndcIndex)).not.toContain(
      data.concepts.findIndex((c) => c.rxcui === '900003')
    );
  });

  it('drops an NDC attribute row whose RXCUI never became a built concept', () => {
    expect(Object.keys(data.ndcIndex)).not.toContain('99999999999');
  });

  it('normalizes a dashed NDC to the same 11-digit key parseNdc would produce', () => {
    expect(data.ndcIndex['11111000101']).toBeDefined();
  });

  it('carries the generatedAt/source/attribution header fields', () => {
    expect(data.generatedAt).toBe('2026-01-01T00:00:00.000Z');
    expect(data.source).toBe('fixture');
    expect(data.attribution.length).toBeGreaterThan(0);
  });
});
