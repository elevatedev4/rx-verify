import { describe, it, expect } from 'vitest';
import {
  parseSharedStringsXml,
  parseSheetRowsXml,
  mapRowsToCatalogSourceRows,
  buildCatalogDataset,
  type CatalogSourceRow
} from '../scripts/build-catalog-data.js';

// Tiny SYNTHETIC sheetXml/sharedStringsXml fixtures, hand-built to
// mirror the REAL export's structure (recon against a real 35,081-row
// McKesson catalog export): shared-string cells (t="s"), a bare numeric
// literal cell (Dea Schedule), and — critically — a SELF-CLOSING <v/>
// for a blank-but-typed GCN cell, which is the exact shape that broke a
// naive single-regex parser during development (it let the empty cell's
// non-greedy match run past it and swallow the next cell's value). All
// descriptions/NDCs/GCNs here are INVENTED, never real catalog rows (see
// this repo's README and scripts/build-catalog-data.ts's header — the
// real catalog is proprietary and its source .xlsx is never committed).
describe('parseSharedStringsXml', () => {
  it('extracts <si><t> entries in order (index = shared-string id)', () => {
    const xml = `<sst><si><t>Supplier Name</t></si><si><t>ZYLODRINE TAB 10MG GX 100</t></si></sst>`;
    expect(parseSharedStringsXml(xml)).toEqual(['Supplier Name', 'ZYLODRINE TAB 10MG GX 100']);
  });

  it('decodes XML entities and concatenates multiple <t> runs (rich text)', () => {
    const xml = `<sst><si><t>A &amp; B</t></si><si><t xml:space="preserve">foo</t><t>bar</t></si></sst>`;
    expect(parseSharedStringsXml(xml)).toEqual(['A & B', 'foobar']);
  });
});

describe('parseSheetRowsXml', () => {
  const sharedStrings = ['McKesson', 'Generic', 'ZYLODRINE TAB 10MG GX 100', '00000-0001-01', '6334'];

  it('resolves shared-string cells and a bare numeric literal cell', () => {
    const xml = `<worksheet><sheetData>
      <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
    </sheetData></worksheet>`;
    const rows = parseSheetRowsXml(xml, sharedStrings);
    expect(rows).toEqual([{ A: 'McKesson', B: 'Generic' }]);
  });

  it('does NOT let a self-closing blank <v/> cell swallow the next cell\'s value (the real bug this parser was fixed for)', () => {
    const xml = `<worksheet><sheetData>
      <row r="2">
        <c r="A2" t="s"><v>2</v></c>
        <c r="D2" t="s"><v>3</v></c>
        <c r="F2" s="2" t="str"><v/></c>
        <c r="G2" s="2"><v>0</v></c>
      </row>
    </sheetData></worksheet>`;
    const rows = parseSheetRowsXml(xml, sharedStrings);
    expect(rows).toEqual([{ A: 'ZYLODRINE TAB 10MG GX 100', D: '00000-0001-01', G: '0' }]);
    // Critically: F must be ABSENT (blank), not accidentally populated
    // with the "0" that belongs to G.
    expect(rows[0]).not.toHaveProperty('F');
  });

  it('treats a fully self-closing <c .../> cell as blank', () => {
    const xml = `<worksheet><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"/></row></sheetData></worksheet>`;
    const rows = parseSheetRowsXml(xml, sharedStrings);
    expect(rows[0]).toEqual({ A: 'McKesson' });
  });
});

describe('mapRowsToCatalogSourceRows', () => {
  it('maps by HEADER TEXT (case-insensitive), not fixed column letters', () => {
    const allRows = [
      { A: 'Description', B: 'NDC', C: 'GCN' }, // reordered vs the real export's A-G layout
      { A: 'ZYLODRINE TAB 10MG', B: '00000-0001-01', C: '5001' }
    ];
    expect(mapRowsToCatalogSourceRows(allRows)).toEqual([
      { description: 'ZYLODRINE TAB 10MG', ndc: '00000-0001-01', gcn: '5001' }
    ]);
  });

  it('ignores an unrecognized header column rather than erroring', () => {
    const allRows = [
      { A: 'NDC', B: 'Some Future Column' },
      { A: '00000-0001-01', B: 'whatever' }
    ];
    expect(mapRowsToCatalogSourceRows(allRows)).toEqual([{ ndc: '00000-0001-01' }]);
  });

  it('returns an empty array when there are no rows at all', () => {
    expect(mapRowsToCatalogSourceRows([])).toEqual([]);
  });
});

describe('buildCatalogDataset', () => {
  const ROWS: CatalogSourceRow[] = [
    { supplierName: 'McKesson', brandGeneric: 'Generic', description: 'ZYLODRINE TAB 10MG GX 100', ndc: '11111-0001-01', gcn: '5001', deaSchedule: '0' },
    { supplierName: 'McKesson', brandGeneric: 'Generic', description: 'ZYLODRINE TAB 10MG APO 100', ndc: '11111-0001-02', gcn: '5001', deaSchedule: '0' },
    { supplierName: 'McKesson', brandGeneric: 'Brand', description: 'ZYLEXA TAB 10MG PFIZ 100', ndc: '22222-0002-01', gcn: '5001', deaSchedule: '0' },
    // Missing NDC -- must be skipped and counted.
    { supplierName: 'McKesson', brandGeneric: 'Generic', description: 'NO NDC ROW', ndc: undefined, gcn: '9999', deaSchedule: '0' },
    // Duplicate NDC (same as row 1) -- first wins, counted separately.
    { supplierName: 'McKesson', brandGeneric: 'Generic', description: 'ZYLODRINE TAB 10MG GX 100 DUP', ndc: '11111-0001-01', gcn: '5001', deaSchedule: '0' },
    // No GCN at all.
    { supplierName: 'McKesson', brandGeneric: 'Generic', description: 'BIMOXATIN CAP 25MG NOGCN 30', ndc: '33333-0003-01', gcn: undefined, deaSchedule: '0' }
  ];

  const { data, stats } = buildCatalogDataset(ROWS, { generatedAt: '2026-01-01T00:00:00.000Z', source: 'fixture' });

  it('indexes each NDC (normalized to 11 digits), first wins on a duplicate', () => {
    expect(stats.uniqueNdcCount).toBe(4);
    expect(stats.duplicateNdcRowsSkipped).toBe(1);
    expect(data.ndcIndex['11111000101']?.description).toBe('ZYLODRINE TAB 10MG GX 100');
  });

  it('counts a missing-NDC row separately from a duplicate', () => {
    expect(stats.skippedMissingNdc).toBe(1);
  });

  it('counts rows with no GCN', () => {
    expect(stats.missingGcnCount).toBe(1);
    expect(data.ndcIndex['33333000301']?.gcn).toBeNull();
  });

  it('gcnCounts tallies DISTINCT NDCs per GCN (not raw rows, so the skipped duplicate is not double-counted)', () => {
    expect(data.gcnCounts['5001']).toBe(3); // the two zylodrine NDCs + the brand NDC
    expect(stats.uniqueGcnCount).toBe(1);
  });

  it('nameIndex carries every leading-prefix key of the normalized description, reachable by a shorter query', () => {
    // A realistic entered-name-length query ("zylodrine tablet 10 mg", 4
    // tokens) must hit a key even though the indexed description is
    // longer ("...gx 100", 6 tokens) -- this is the exact reason
    // catalogNameIndexKeys generates every prefix length, not just the
    // full string.
    expect(data.nameIndex['zylodrine tablet 10 mg']).toContain('11111000101');
    expect(data.nameIndex['zylodrine tablet 10 mg']).toContain('11111000102');
  });

  it('carries the header fields through', () => {
    expect(data.generatedAt).toBe('2026-01-01T00:00:00.000Z');
    expect(data.source).toBe('fixture');
    expect(data.attribution.length).toBeGreaterThan(0);
  });
});

describe('NDC normalization (via parseNdc, reused not reimplemented)', () => {
  it.each([
    ['5-4-2 dashed (already 11 digits)', '11111-0001-01', '11111000101'],
    ['4-4-2 10-digit dashed (labeler padded)', '1111-0001-01', '01111000101'],
    ['5-3-2 10-digit dashed (product padded)', '11111-001-01', '11111000101'],
    ['5-4-1 10-digit dashed (package padded)', '11111-0001-1', '11111000101'],
    ['bare 11-digit', '11111000101', '11111000101']
  ])('%s', (_label, raw, expected) => {
    const { data } = buildCatalogDataset([{ description: 'X', ndc: raw, gcn: '1' }]);
    expect(Object.keys(data.ndcIndex)).toEqual([expected]);
  });

  it('a bare (undelimited) 10-digit NDC is ambiguous and is skipped, never guessed', () => {
    const { data, stats } = buildCatalogDataset([{ description: 'X', ndc: '1111000101', gcn: '1' }]);
    expect(Object.keys(data.ndcIndex)).toEqual([]);
    expect(stats.skippedUnparseableNdc).toBe(1);
  });
});
