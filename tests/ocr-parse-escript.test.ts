import { describe, it, expect } from 'vitest';
import { parseEscriptOcr, type OcrWord } from '../src/ocr/parseEscriptOcr.js';

/**
 * SYNTHETIC DATA ONLY — every name/DOB/NPI/NDC/address/phone below is
 * fabricated for this test file and does not correspond to any real
 * patient, prescriber, or prescription.
 *
 * Builds one "row" of OcrWord bounding boxes at a given Y for a list of
 * whitespace-separated tokens, laid out left-to-right. Multiple tokens
 * can be passed as separate strings to simulate OCR word-splitting
 * (e.g. row(y, ['Di', 'recti', 'ons:'])).
 */
function row(y: number, tokens: string[]): OcrWord[] {
  return tokens.map((text, i) => ({ text, x: i * 90, y, w: 80, h: 18 }));
}

function flatten(rows: OcrWord[][]): OcrWord[] {
  return rows.flat();
}

/** The toolbar/chrome row observed in the real dump (abstracted) — always placed above the data rows in these fixtures, at y=0. */
const TOOLBAR_ROW = row(0, [
  'Dispense',
  '|',
  'Image',
  '[1]',
  'New',
  'Prescription',
  'DS:',
  '30',
  'Escript',
  '[3]',
  'DUR/More',
  '[4]',
  'Workflow/Claims',
  '[5]',
  '55.0%',
  'Rx',
  'Edits',
  '[6]',
  'Fill',
  'Audit',
  '[7]',
  'Original',
  'Refilled',
  '1',
  'time',
  'Zoom',
  'Select'
]);

describe('parseEscriptOcr', () => {
  it('(a) parses the labels-block-then-values-block layout, skipping the toolbar', () => {
    const labelRows = [
      row(100, ['Patient']),
      row(120, ['Address:']),
      row(140, ['DOB']),
      row(160, ['Prescriber']),
      row(180, ['Location:']),
      row(200, ['Phone']),
      row(220, ['Written']),
      row(240, ['NDC']),
      row(260, ['Medication']),
      row(280, ['Quantity']),
      row(300, ['Directions:']),
      row(320, ['Note']),
      row(340, ['Substitutions'])
    ];
    const valueRows = [
      row(360, ['Sample,', 'Pat', 'Q']),
      row(380, ['123', 'SYNTH', 'ST', 'FAKETOWN,', 'KS', '660001111']),
      row(400, ['01/02/1970']),
      row(420, ['Demo,', 'Dana']),
      row(440, ['456', 'MOCK', 'AVE', 'FAKETOWN,', 'KS', '660002222']),
      row(460, ['(555)', '555-0100']),
      row(480, ['07/01/2026']),
      row(500, ['00000000011']),
      row(520, ['Clindamycin', 'Lotion']),
      row(540, ['30']),
      row(560, ['TAKE', '1', 'TABLET', 'BY', 'MOUTH', 'DAILY']),
      row(580, ['See', 'pharmacist']),
      row(600, ['substitution', 'not', 'allowed'])
    ];
    // NPI (10-digit, unlabeled) sits alongside the prescriber's phone row —
    // a plausible on-screen shape where it has no label slot of its own.
    valueRows[5]?.push({ text: '1234567890', x: 400, y: 460, w: 90, h: 18 });

    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Sample, Pat Q');
    expect(record.patientDOB).toBe('01/02/1970');
    expect(record.patientAddress).toEqual({
      street: '123 SYNTH ST',
      city: 'FAKETOWN',
      state: 'KS',
      zip: '66000'
    });
    expect(record.prescriber?.name).toBe('Demo, Dana');
    expect(record.prescriber?.address).toEqual({
      street: '456 MOCK AVE',
      city: 'FAKETOWN',
      state: 'KS',
      zip: '66000'
    });
    expect(record.prescriber?.npi).toBe('1234567890');
    expect(record.dateWritten).toBe('07/01/2026');
    expect(record.drug?.ndc).toBe('00000000011');
    expect(record.drug?.name).toBe('Clindamycin Lotion');
    expect(record.quantity).toBe('30');
    expect(record.sig).toBe('TAKE 1 TABLET BY MOUTH DAILY');
    expect(record.substitutionsNotAllowed).toBe(true);
  });

  it('(b) parses a clean one-label-per-line "Label: value" layout', () => {
    const rows = [
      row(100, ['Patient:', 'Roe,', 'Jamie']),
      row(120, ['DOB:', '05/06/1985']),
      row(140, ['Prescriber:', 'Alt,', 'Robin']),
      row(160, ['Phone:', '(555)', '222-3333']),
      row(180, ['Medication:', 'Amoxicillin', '500mg']),
      row(200, ['NDC:', '12345678901']),
      row(220, ['Quantity:', '20', 'EA']),
      row(240, ['Directions:', 'TAKE', '1', 'CAPSULE', 'TWICE', 'DAILY']),
      row(260, ['Substitutions:', 'substitution', 'allowed'])
    ];
    const ocr = flatten([TOOLBAR_ROW, ...rows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Roe, Jamie');
    expect(record.patientDOB).toBe('05/06/1985');
    expect(record.prescriber?.name).toBe('Alt, Robin');
    expect(record.prescriber?.phone).toBe('(555) 222-3333');
    expect(record.drug?.name).toBe('Amoxicillin 500mg');
    expect(record.drug?.ndc).toBe('12345678901');
    expect(record.quantity).toBe('20');
    expect(record.quantityUnit).toBe('EA');
    expect(record.sig).toBe('TAKE 1 CAPSULE TWICE DAILY');
    expect(record.substitutionsNotAllowed).toBe(false);
  });

  it('(c) tolerates OCR noise: word-split and letter-swapped labels', () => {
    const rows = [
      row(100, ['Pati', 'ent:', 'Noise,', 'Test']),
      // "Di recti ons:" -> "Directions:" split across 3 words.
      row(120, ['Di', 'recti', 'ons:', 'APPLY', 'TOPICALLY', 'DAILY']),
      // "Writtem" (letter swap m<->n) instead of "Written".
      row(140, ['Writtem:', '03/04/2026']),
      // "CIaims" — capital I instead of lowercase l — not a label at all,
      // just proving noisy non-label chrome-ish words don't get matched
      // as a field once past the toolbar-skip boundary.
      row(160, ['Escri', 'pt:', 'reference', 'only'])
    ];
    const ocr = flatten([TOOLBAR_ROW, ...rows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Noise, Test');
    expect(record.sig).toBe('APPLY TOPICALLY DAILY');
    expect(record.dateWritten).toBe('03/04/2026');
  });

  it('(d) leaves a blank/missing field as undefined without misaligning the rest', () => {
    const labelRows = [row(100, ['Patient']), row(120, ['DOB']), row(140, ['Prescriber'])];
    // DOB has NO corresponding value row at all — value block is one
    // short. Prescriber's value must still land correctly, not slide
    // into DOB's slot.
    const valueRows = [row(160, ['Gap,', 'Case']), row(180, ['Alt,', 'Robin'])];

    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Gap, Case');
    // DOB borrowed the only remaining leftover line ("Alt, Robin"), which
    // doesn't parse as a date, so date-validation correctly leaves it
    // unset rather than accepting a non-date value.
    expect(record.patientDOB).toBeUndefined();
    expect(record.prescriber?.name).toBeUndefined();
  });

  it('(e) uses NPI/NDC/date/ZIP patterns to disambiguate, even when a value lands under the wrong label slot', () => {
    // Simulates the exact failure mode described in the branch brief: a
    // block-layout shift puts the 10-digit NPI under the "Written" label
    // slot instead of a real date, and the true written date is a
    // leftover value with no label slot mapped to it at all.
    const labelRows = [row(100, ['Written']), row(120, ['NDC']), row(140, ['DOB'])];
    const valueRows = [
      row(160, ['1234567890']), // lands under "Written" but is actually the NPI
      row(180, ['00000000011']), // correctly under "NDC"
      row(200, ['660001234']) // lands under "DOB" but is actually a 9-digit ZIP fragment, not a date
    ];
    // The real written date is present in the capture but outside any
    // label's positional slot (e.g. wrapped onto its own extra line).
    const extraRow = row(220, ['07/01/2026']);

    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows, extraRow]);
    const record = parseEscriptOcr(ocr);

    // NPI found by pure 10-digit pattern, regardless of the "Written" label.
    expect(record.prescriber?.npi).toBe('1234567890');
    // NDC found by pure 11-digit pattern.
    expect(record.drug?.ndc).toBe('00000000011');
    // "Written" and "DOB" both got non-date values positionally; the
    // fallback pool has exactly one genuinely date-shaped candidate
    // ("07/01/2026"), so it's claimed by whichever date field asks first
    // (dob, in field-assembly order) rather than silently accepting the
    // 9-digit ZIP-shaped junk as a date.
    expect(record.patientDOB).toBe('07/01/2026');
    expect(record.dateWritten).toBeUndefined();
  });

  it('never throws on garbage/empty input and returns a blank record', () => {
    expect(parseEscriptOcr([])).toEqual({});
    expect(parseEscriptOcr(null)).toEqual({});
    expect(parseEscriptOcr(undefined)).toEqual({});
    expect(
      parseEscriptOcr([
        { text: '###', x: 0, y: 0, w: 5, h: 5 },
        { text: '', x: 5, y: 0, w: 5, h: 5 }
      ])
    ).toEqual({});
  });
});
