import { describe, it, expect } from 'vitest';
import {
  parseEscriptOcr,
  repairDigits,
  buildDiagnosticsBlock,
  type OcrWord,
  type FieldDiagnostic
} from '../src/ocr/parseEscriptOcr.js';

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
    // CHANGED expectation (branch brief): "EA" now folds to undefined —
    // it's not a real unit on the live e-script layout, same as
    // Unspecified/Unit/each. Was 'EA' before this branch.
    expect(record.quantityUnit).toBeUndefined();
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

  it('(f) [live-tuning fixture 1] dashed NDC, quantity/written/refills extraction, and label-noise boundaries — mirrors a real e-script capture (PHI replaced)', () => {
    // SYNTHETIC — fabricated for this test, mirrors the shape of a real
    // owner capture with the PHI replaced. Deliberately merges a couple
    // of noise tokens onto the SAME row as an adjacent field's value (as
    // observed on the live layout) to exercise the branch's noise-trim
    // fix; every other field keeps the clean label-block/value-block
    // shape from fixture (a).
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
      row(320, ['Refills']),
      row(340, ['Substitutions'])
    ];
    const valueRows = [
      // "/ Mab" bleed (branch brief defect #3, item 1) — "Mab" is
      // pharmacy chrome that landed on the patient-name row.
      row(360, ['Sample,', 'Pat', 'Q', 'Mab']),
      row(380, ['100', 'MAPLE', 'ST', 'FAKETOWN', 'KS660000000']),
      row(400, ['01/02/1970']),
      // "Agent name" bleed (defect #3, item 2).
      row(420, ['Demo,', 'Dana', 'Agent', 'name']),
      // Trailing license number bleed (defect #3, item 3 variant) —
      // must NOT end up glued into the parsed street/zip.
      row(440, ['200', 'OAK', 'AVE', 'FAKETOWN,', 'KS', '660001111', '5380389']),
      // "spr <SPI digits>" bleed (defect #3, item 3 as literally observed).
      row(460, ['(555)', '555-0199', 'spr', '1526938475001']),
      row(480, ['03/03/2026']),
      // Dashed NDC (defect #1) — v1 missed this entirely.
      row(500, ['00168-0203-60']),
      row(520, ['CLINDAMYCIN', 'PHOSPHATE', '1%', 'LOTION']),
      // "50.0000 Unspecified" (defect #2) — quantity + unit-fold.
      row(540, ['50.0000', 'Unspecified']),
      row(560, ['Apply', 'to', 'scalp', 'Externally', 'Twice', 'a', 'day', 'when', 'needed', 'for', 'flares']),
      // "1 (additional refills)" (defect #2) — refills wasn't extracted
      // at all in v1. The bare 10-digit NPI rides along on this same
      // row (a plausible on-screen shape, per fixture (a)'s NPI-on-
      // phone-row precedent) — parseRefills only reads the LEADING
      // integer, so it doesn't interfere.
      row(580, ['1', '(additional', 'refills)', '1234567890']),
      row(600, ['Allowed'])
    ];

    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Sample, Pat Q');
    expect(record.patientAddress).toEqual({
      street: '100 MAPLE ST',
      city: 'FAKETOWN',
      state: 'KS',
      zip: '66000'
    });
    expect(record.patientDOB).toBe('01/02/1970');
    // NOT "Demo, Dana Agent name".
    expect(record.prescriber?.name).toBe('Demo, Dana');
    // License number "5380389" must not be glued into the address.
    expect(record.prescriber?.address).toEqual({
      street: '200 OAK AVE',
      city: 'FAKETOWN',
      state: 'KS',
      zip: '66000'
    });
    expect(record.prescriber?.npi).toBe('1234567890');
    // NOT "(555) 555-0199 spr 1526938475001".
    expect(record.prescriber?.phone).toBe('(555) 555-0199');
    expect(record.dateWritten).toBe('03/03/2026');
    // Dashed NDC recognized (bare-digit or dashed both acceptable; we
    // keep the matched dashed token as-is per branch brief).
    expect(record.drug?.ndc).toBe('00168-0203-60');
    expect(record.drug?.name).toBe('CLINDAMYCIN PHOSPHATE 1% LOTION');
    expect(record.quantity).toBe('50.0000');
    expect(record.quantityUnit).toBeUndefined();
    expect(record.sig).toBe('Apply to scalp Externally Twice a day when needed for flares');
    expect(record.refills).toBe('1');
    expect(record.substitutionsNotAllowed).toBe(false);
  });

  it('(g) [live-tuning fixture 2] messier capture: unit in address, mangled written date, mangled phone, alternate dashed NDC', () => {
    // SYNTHETIC — messier variant per branch brief. Only asserting the
    // critical numeric/dated fields resolve and that text fields don't
    // absorb noise; the mangled phone's exact text isn't asserted (only
    // that it doesn't corrupt other fields) since best-effort repair for
    // it isn't in scope.
    const labelRows = [
      row(100, ['Patient']),
      row(120, ['Address:']),
      row(140, ['DOB']),
      row(160, ['Prescriber']),
      row(180, ['Location:']),
      row(200, ['Phone']),
      row(220, ['Written']),
      row(240, ['NDC']),
      row(260, ['Medication'])
    ];
    const valueRows = [
      row(360, ['Roe,', 'Jamie', 'SN']),
      row(380, ['500', 'ELM', 'ST', 'Suite', '202', 'FAKETOWN,', 'KS', '660009999']),
      row(400, ['05/06/1985']),
      row(420, ['Alt,', 'Robin']),
      // NPI rides along on the location row (10-digit, trailing) — must
      // still resolve even though it's noise for the address parse.
      row(440, ['300', 'PINE', 'RD', 'FAKETOWN', 'KS', '660008888', '1234567890']),
      // OCR-mangled phone (branch brief literal example) — not asserted
      // exactly, just must not corrupt neighboring fields.
      row(460, ['085)', '256-9632']),
      // Mangled written date: missing "/" between day and year.
      row(480, ['07/022026']),
      // Alternate dashed NDC shape (5-4-2).
      row(500, ['82619-0105-01']),
      row(520, ['AMOXICILLIN', '500MG'])
    ];

    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    // "SN" chrome bleed stripped from patient name.
    expect(record.patientName).toBe('Roe, Jamie');
    expect(record.patientAddress?.city).toBe('FAKETOWN');
    expect(record.patientAddress?.state).toBe('KS');
    expect(record.patientAddress?.zip).toBe('66000');
    expect(record.patientDOB).toBe('05/06/1985');
    expect(record.prescriber?.name).toBe('Alt, Robin');
    // NPI still found by pure 10-digit pattern despite riding on the
    // address row.
    expect(record.prescriber?.npi).toBe('1234567890');
    expect(record.prescriber?.phone).toBeDefined();
    // Repaired mangled date: "07/022026" -> "07/02/2026".
    expect(record.dateWritten).toBe('07/02/2026');
    expect(record.drug?.ndc).toBe('82619-0105-01');
    expect(record.drug?.name).toBe('AMOXICILLIN 500MG');
  });

  it('(h) [live-tuning fixture 3] recognizes "Refills Authorized" as a label variant instead of leaving "Authorized" behind as the value', () => {
    const rows = [
      row(100, ['Patient:', 'Test,', 'Case']),
      // Real on-screen label per owner report — plain "refills" alone is
      // 10+ edits away under the fuzzy threshold, so this needs its own
      // canonical variant (see LABELS) plus the longer-match tie-break.
      row(120, ['Refills', 'Authorized:', '2'])
    ];
    const ocr = flatten([TOOLBAR_ROW, ...rows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Test, Case');
    expect(record.refills).toBe('2');
  });

  it('(h) refills value "l" (OCR letter-for-digit) repairs to "1"', () => {
    const rows = [row(100, ['Patient:', 'Test,', 'Case']), row(120, ['Refills:', 'l'])];
    const ocr = flatten([TOOLBAR_ROW, ...rows]);
    const record = parseEscriptOcr(ocr);

    expect(record.refills).toBe('1');
  });

  it('(h) written date "O7/02/2026" (letter O for digit 0) repairs to "07/02/2026"', () => {
    const rows = [row(100, ['Patient:', 'Test,', 'Case']), row(120, ['Written:', 'O7/02/2026'])];
    const ocr = flatten([TOOLBAR_ROW, ...rows]);
    const record = parseEscriptOcr(ocr);

    expect(record.dateWritten).toBe('07/02/2026');
  });

  it('(h) written date "07-02-2026" (dash separators) normalizes to "07/02/2026"', () => {
    const rows = [row(100, ['Patient:', 'Test,', 'Case']), row(120, ['Written:', '07-02-2026'])];
    const ocr = flatten([TOOLBAR_ROW, ...rows]);
    const record = parseEscriptOcr(ocr);

    expect(record.dateWritten).toBe('07/02/2026');
  });

  it('(h) quantity "5O.0000 Unspecified" (letter O for digit 0) repairs to "50.0000"', () => {
    const rows = [
      row(100, ['Patient:', 'Test,', 'Case']),
      row(120, ['Quantity:', '5O.0000', 'Unspecified'])
    ];
    const ocr = flatten([TOOLBAR_ROW, ...rows]);
    const record = parseEscriptOcr(ocr);

    expect(record.quantity).toBe('50.0000');
    expect(record.quantityUnit).toBeUndefined();
  });

  it('(h) repairDigits does NOT alter a clearly alphabetic token (never applied to names/drug text)', () => {
    // "LOTION"/"CLINDAMYCIN" both contain letters that are lookalikes for
    // digits (O, I) but also contain letters outside the lookalike set
    // (L, T, N, C, D, M, Y) — repairDigits must leave real words alone.
    expect(repairDigits('LOTION')).toBe('LOTION');
    expect(repairDigits('CLINDAMYCIN')).toBe('CLINDAMYCIN');
  });

  it('(i) [live-tuning fixture 4] Quantity+Refills share one physical OCR row (real on-screen shape from a live capture, PHI replaced) — both must resolve, not just the leading field', () => {
    // SYNTHETIC — geometry (x/y/w/h) copied verbatim from a real owner
    // capture; only the text content is fabricated. On this real screen,
    // PioneerRx renders "Quantity <value>" and "Refills <value>" as TWO
    // label:value pairs packed onto the SAME visual row (Quantity's label
    // is far left, its value sits just right of it; Refills' label/value
    // sit much further right on that same row). v1 grouped the whole row
    // into one OcrWord[] line, and the "Refills" text collided with the
    // CHROME_TOKENS 'refill'/'refilled' entries (added to filter the
    // "Refilled 1 time" tab-strip) via isChromeLine's 2-fuzzy-hit rule —
    // "Refills" and "refills)" alone supply 2 hits, so the ENTIRE row
    // (including the real Quantity value) got dropped as chrome.
    const patientRow = [
      { text: 'Sample,', x: 120, y: 95, w: 96, h: 14 } as OcrWord,
      { text: 'Q', x: 220, y: 95, w: 64, h: 12 }
    ];
    // Real coordinates from the live capture's Quantity/Refills row.
    const quantityRefillsRow = [
      { text: 'Quantity', x: 57, y: 358, w: 50, h: 13 } as OcrWord,
      { text: '6.0000', x: 123, y: 358, w: 58, h: 11 },
      { text: 'Refills', x: 593, y: 358, w: 37, h: 10 },
      { text: '1', x: 639, y: 358, w: 6, h: 11 },
      { text: '(additional', x: 650, y: 358, w: 77, h: 15 },
      { text: 'refills)', x: 731, y: 358, w: 41, h: 15 }
    ];
    const ocr = flatten([TOOLBAR_ROW, row(100, ['Patient']), patientRow, quantityRefillsRow]);
    const record = parseEscriptOcr(ocr);

    expect(record.quantity).toBe('6.0000');
    expect(record.refills).toBe('1');
  });

  it('(i) [live-tuning fixture 4] Directions/sig row picks up a stray far-column token — must be excluded, and the "O." + digit OCR split repairs to "0.5"', () => {
    // SYNTHETIC — geometry copied verbatim from the same live capture;
    // text fabricated. The split label "Di recti ons:" (real OCR
    // word-splitting) sits on a row that ALSO groups in a stray token
    // ("90", real days-supply value that lands in a different on-screen
    // column far to the right — same X band as the Refills column above)
    // because both rows merge into one OcrWord[] line by Y-proximity. Sig
    // must stop before the far-right stray token, and "O. 5" (OCR
    // splitting "0.5" into two words around the period) must repair to
    // "0.5" with no space.
    const directionsRow = [
      { text: 'Di', x: 48, y: 381, w: 11, h: 11 } as OcrWord,
      { text: 'recti', x: 61, y: 381, w: 23, h: 11 },
      { text: 'ons:', x: 85, y: 384, w: 22, h: 8 },
      { text: 'O.', x: 121, y: 381, w: 13, h: 12 },
      { text: '5', x: 136, y: 381, w: 9, h: 12 },
      { text: 'ML', x: 148, y: 381, w: 22, h: 12 },
      { text: 'Subcutaneous', x: 174, y: 381, w: 103, h: 12 },
      { text: 'weekly', x: 279, y: 381, w: 49, h: 15 },
      // Stray bleed from a far-right column on the same visual row.
      { text: '90', x: 637, y: 381, w: 16, h: 11 }
    ];
    const ocr = flatten([
      TOOLBAR_ROW,
      row(100, ['Patient']),
      [{ text: 'Sample,', x: 120, y: 95, w: 96, h: 14 } as OcrWord, { text: 'Q', x: 220, y: 95, w: 64, h: 12 }],
      row(533, ['Note']),
      directionsRow
    ]);
    const record = parseEscriptOcr(ocr);

    expect(record.sig).toBe('0.5 ML Subcutaneous weekly');
  });

  it('(j) [live-tuning fixture 5] Quantity+Refills share one physical LABEL-ONLY row (no inline values at all) in the labels-block-then-values-block layout — positional Pass B pairing must not shift downstream fields', () => {
    // SYNTHETIC — mirrors the real PioneerRx block layout where the
    // LABELS column packs "Quantity" and "Refills" onto one physical row
    // (label-only, no values between them — unlike fixture 4 above, which
    // has inline values). Before this fix, Pass A's label-only row only
    // ever recognized the FIRST label ('quantity') and swallowed the
    // second label's own text ("Refills") as if it were quantity's value,
    // so 'refills' never even entered labelOrder — Pass B's positional
    // label-to-leftover-line pairing then shifted every field after
    // quantity/refills (directions/note/substitutions) by one slot.
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
      [
        { text: 'Quantity', x: 0, y: 280, w: 80, h: 18 } as OcrWord,
        { text: 'Refills', x: 500, y: 280, w: 80, h: 18 }
      ],
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
      [
        { text: '30', x: 0, y: 540, w: 80, h: 18 } as OcrWord,
        { text: '2', x: 500, y: 540, w: 80, h: 18 }
      ],
      row(560, ['TAKE', '1', 'TABLET', 'BY', 'MOUTH', 'DAILY']),
      row(580, ['See', 'pharmacist']),
      row(600, ['substitution', 'not', 'allowed'])
    ];
    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.quantity).toBe('30');
    expect(record.refills).toBe('2');
    expect(record.sig).toBe('TAKE 1 TABLET BY MOUTH DAILY');
    expect(record.substitutionsNotAllowed).toBe(true);
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

  // -----------------------------------------------------------------
  // Geometry-remap fixtures (this branch — see class doc "GEOMETRY
  // REMAP"). Fixtures (a)-(j) above encode the owner's real capture
  // geometry and must keep passing UNCHANGED; these add layout variants
  // the earlier ordinal (list-position) Pass B pairing couldn't handle
  // correctly, per the branch brief.
  // -----------------------------------------------------------------

  it('(k) block layout scrolled/shifted vertically — a uniform Y offset does not affect label/value pairing', () => {
    const Y_SHIFT = 1000;
    const shift = (rows: OcrWord[][]) => rows.map((r) => r.map((w) => ({ ...w, y: w.y + Y_SHIFT })));
    const labelRows = shift([
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
    ]);
    const valueRows = shift([
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
    ]);

    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Sample, Pat Q');
    expect(record.patientDOB).toBe('01/02/1970');
    expect(record.prescriber?.name).toBe('Demo, Dana');
    expect(record.dateWritten).toBe('07/01/2026');
    expect(record.drug?.ndc).toBe('00000000011');
    expect(record.drug?.name).toBe('Clindamycin Lotion');
    expect(record.quantity).toBe('30');
    expect(record.sig).toBe('TAKE 1 TABLET BY MOUTH DAILY');
    expect(record.substitutionsNotAllowed).toBe(true);
  });

  it('(l) value column offset from label column — values render at a fixed X offset from their labels', () => {
    const X_OFFSET = 250;
    const offsetRow = (y: number, tokens: string[]): OcrWord[] =>
      tokens.map((text, i) => ({ text, x: X_OFFSET + i * 90, y, w: 80, h: 18 }));
    const labelRows = [
      row(100, ['Patient']),
      row(120, ['DOB']),
      row(140, ['Prescriber']),
      row(160, ['Written']),
      row(180, ['Quantity'])
    ];
    const valueRows = [
      offsetRow(220, ['Roe,', 'Jamie']),
      offsetRow(240, ['05/06/1985']),
      offsetRow(260, ['Alt,', 'Robin']),
      offsetRow(280, ['07/01/2026']),
      offsetRow(300, ['40'])
    ];
    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Roe, Jamie');
    expect(record.patientDOB).toBe('05/06/1985');
    expect(record.prescriber?.name).toBe('Alt, Robin');
    expect(record.dateWritten).toBe('07/01/2026');
    expect(record.quantity).toBe('40');
  });

  it('(m) partial capture — bottom fields absent yield clean MISSes, not values shifted up into the wrong field', () => {
    const labelRows = [
      row(100, ['Patient']),
      row(120, ['DOB']),
      row(140, ['Prescriber']),
      row(160, ['Phone']),
      row(180, ['Written']),
      row(200, ['NDC']),
      row(220, ['Medication']),
      row(240, ['Quantity']),
      row(260, ['Refills'])
    ];
    // Capture was scrolled/cut off — only the first two fields' values
    // actually made it into the OCR read; everything below is blank on
    // screen, not merely unlabeled.
    const valueRows = [row(300, ['Gap,', 'Case']), row(320, ['01/02/1970'])];

    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Gap, Case');
    expect(record.patientDOB).toBe('01/02/1970');
    expect(record.prescriber?.name).toBeUndefined();
    expect(record.prescriber?.phone).toBeUndefined();
    expect(record.dateWritten).toBeUndefined();
    expect(record.drug?.ndc).toBeUndefined();
    expect(record.drug?.name).toBeUndefined();
    expect(record.quantity).toBeUndefined();
    expect(record.refills).toBeUndefined();
  });

  it('(n) extra far-right column present on every value row — never absorbed into an unrelated field', () => {
    // A far-right annotation column (e.g. an on-screen flag/checkbox
    // readout that isn't any known field) rides along on every value
    // row's Y band, well past normal within-value word spacing — general
    // form of the row-grouping-jitter root cause (branch brief), no
    // longer limited to the sig/directions field.
    const FAR_X = 900;
    const withFarColumn = (r: OcrWord[]): OcrWord[] => [
      ...r,
      { text: 'FLAG', x: FAR_X, y: (r[0] as OcrWord).y, w: 40, h: 18 }
    ];
    const labelRows = [
      row(100, ['Patient']),
      row(120, ['DOB']),
      row(140, ['Prescriber']),
      row(160, ['Written']),
      row(180, ['Quantity'])
    ];
    const valueRows = [
      withFarColumn(row(220, ['Roe,', 'Jamie'])),
      withFarColumn(row(240, ['05/06/1985'])),
      withFarColumn(row(260, ['Alt,', 'Robin'])),
      withFarColumn(row(280, ['07/01/2026'])),
      withFarColumn(row(300, ['40']))
    ];
    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Roe, Jamie');
    expect(record.patientDOB).toBe('05/06/1985');
    expect(record.prescriber?.name).toBe('Alt, Robin');
    expect(record.dateWritten).toBe('07/01/2026');
    expect(record.quantity).toBe('40');
  });

  it('(o) jumbled two-column label block — geometry-column pairing must not swap patient/prescriber', () => {
    // Two side-by-side label sub-panels: Patient info in the left column
    // (x=0), Prescriber in a separate right column (x=500) on its own
    // physical row. Their VALUE rows do NOT land in the same relative
    // top-to-bottom order as their labels — the right panel's value
    // renders ABOVE the left panel's second value here, a real
    // possibility when two side-by-side panels have different row
    // heights/spacing. A naive "Nth missing label -> Nth leftover line in
    // one shared list" (ordinal) pairing swaps patient and prescriber's
    // names in this exact shape; column-bounded pairing must not.
    const patientLabel: OcrWord[] = [{ text: 'Patient', x: 0, y: 100, w: 80, h: 18 }];
    const prescriberLabel: OcrWord[] = [{ text: 'Prescriber', x: 500, y: 115, w: 100, h: 18 }];
    const dobLabel = row(140, ['DOB']);

    const prescriberValue: OcrWord[] = [
      { text: 'Alt,', x: 500, y: 300, w: 60, h: 18 },
      { text: 'Robin', x: 570, y: 300, w: 70, h: 18 }
    ];
    const patientValue: OcrWord[] = [
      { text: 'Roe,', x: 0, y: 320, w: 60, h: 18 },
      { text: 'Jamie', x: 70, y: 320, w: 70, h: 18 }
    ];
    const dobValue = row(340, ['05/06/1985']);

    const ocr = flatten([
      TOOLBAR_ROW,
      patientLabel,
      prescriberLabel,
      dobLabel,
      prescriberValue,
      patientValue,
      dobValue
    ]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Roe, Jamie');
    expect(record.prescriber?.name).toBe('Alt, Robin');
    expect(record.patientDOB).toBe('05/06/1985');
  });

  it('(p) single label column, one leftover value row x-scattered from the rest — geometry pairing must not lose coverage vs. plain ordinal pairing', () => {
    // REGRESSION for a reviewer-found blocking bug: ONE label column
    // (Patient/DOB/Prescriber/Quantity, every label at x=100 — the
    // owner's ordinary single-column panel) but the Quantity VALUE row
    // lands offset at x=400 (right-justified numeric, inconsistent
    // indentation, or plain bbox jitter past the column-clustering
    // threshold — nothing to do with a real second column). Column-
    // mapped Pass B alone maps every missing label to value-cluster 0
    // only (there's only one label column) and starves the x=400 row —
    // main's plain ordinal pairing resolved this fine. Geometry pairing
    // must never resolve FEWER fields than ordinal did; it may only ever
    // resolve MORE (or the same) — see the fallback in appendix
    // "pickFallback" in parseEscriptOcr.ts.
    const labelRows = [
      row(100, ['Patient']),
      row(120, ['DOB']),
      row(140, ['Prescriber']),
      row(160, ['Quantity'])
    ].map((r) => r.map((w) => ({ ...w, x: w.x + 100 })));
    const valueRows = [
      row(200, ['Roe,', 'Jamie']).map((w) => ({ ...w, x: w.x + 100 })),
      row(220, ['05/06/1985']).map((w) => ({ ...w, x: w.x + 100 })),
      row(240, ['Alt,', 'Robin']).map((w) => ({ ...w, x: w.x + 100 })),
      // Offset to x=400 — same row/field order, just x-scattered.
      row(260, ['40']).map((w) => ({ ...w, x: w.x + 400 }))
    ];
    const ocr = flatten([TOOLBAR_ROW, ...labelRows, ...valueRows]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Roe, Jamie');
    expect(record.patientDOB).toBe('05/06/1985');
    expect(record.prescriber?.name).toBe('Alt, Robin');
    expect(record.quantity).toBe('40');
  });

  it('(q) two-column layout, a label with NO value at all must not steal a later label\'s correctly column-matched value', () => {
    // REGRESSION for a reviewer-found blocking bug reintroduced by the
    // (p) fallback fix: labels are encountered Patient(x=0) ->
    // Prescriber(x=500) -> Phone(x=500) -> DOB(x=0). Phone is genuinely
    // ABSENT from the capture (no value anywhere), so its column-mapped
    // primary attempt fails and it used to fall back INLINE, stealing
    // the next not-yet-consumed leftover line — which was actually DOB's
    // own correctly column-matched value, encountered later in the
    // label list. A single-pass "primary-then-immediate-fallback" loop
    // lets an earlier-starved label steal a later label's good match;
    // the fix runs every label's PRIMARY column-matched attempt first,
    // and only then lets any still-unresolved label fall back — so DOB
    // must resolve its real value and Phone must end up a clean MISS,
    // never a stolen "05/06/1985".
    const patientLabel: OcrWord[] = [{ text: 'Patient', x: 0, y: 100, w: 80, h: 18 }];
    const prescriberLabel: OcrWord[] = [{ text: 'Prescriber', x: 500, y: 115, w: 100, h: 18 }];
    const phoneLabel: OcrWord[] = [{ text: 'Phone', x: 500, y: 130, w: 60, h: 18 }];
    const dobLabel: OcrWord[] = [{ text: 'DOB', x: 0, y: 145, w: 60, h: 18 }];

    const patientValue: OcrWord[] = [
      { text: 'Roe,', x: 0, y: 300, w: 60, h: 18 },
      { text: 'Jamie', x: 70, y: 300, w: 70, h: 18 }
    ];
    const prescriberValue: OcrWord[] = [
      { text: 'Alt,', x: 500, y: 320, w: 60, h: 18 },
      { text: 'Robin', x: 570, y: 320, w: 70, h: 18 }
    ];
    // Phone has NO corresponding value row anywhere in the capture.
    const dobValue: OcrWord[] = [{ text: '05/06/1985', x: 0, y: 340, w: 90, h: 18 }];

    const ocr = flatten([
      TOOLBAR_ROW,
      patientLabel,
      prescriberLabel,
      phoneLabel,
      dobLabel,
      patientValue,
      prescriberValue,
      dobValue
    ]);
    const record = parseEscriptOcr(ocr);

    expect(record.patientName).toBe('Roe, Jamie');
    expect(record.prescriber?.name).toBe('Alt, Robin');
    // The real, unambiguous DOB must resolve — not be silently dropped.
    expect(record.patientDOB).toBe('05/06/1985');
    // Phone was genuinely absent — a clean MISS, not "05/06/1985" stolen
    // from DOB and then rejected by isPhoneShaped.
    expect(record.prescriber?.phone).toBeUndefined();
  });

  describe('buildDiagnosticsBlock (per-field diagnostics log formatting — branch brief item 4)', () => {
    it('renders a resolved field with label/value position and strategy', () => {
      const entries: FieldDiagnostic[] = [
        {
          field: 'patientName',
          status: 'resolved',
          strategy: 'block-column',
          label: { text: 'Patient', x: 0, y: 100 },
          value: { text: 'Roe, Jamie', x: 0, y: 320 }
        }
      ];
      const block = buildDiagnosticsBlock(entries);

      expect(block).toContain('patientName:');
      expect(block).toContain('label "Patient"@(0,100)');
      expect(block).toContain('value "Roe, Jamie"@(0,320)');
      expect(block).toContain('[block-column]');
    });

    it('renders a MISS with its machine-readable reason and no fabricated value', () => {
      const entries: FieldDiagnostic[] = [{ field: 'dateWritten', status: 'miss', reason: 'no-value-paired' }];
      const block = buildDiagnosticsBlock(entries);

      expect(block).toContain('dateWritten: MISS(no-value-paired)');
    });

    it('truncates long value text so the block stays compact (a few lines per field)', () => {
      const longSig = 'A'.repeat(200);
      const entries: FieldDiagnostic[] = [
        { field: 'sig', status: 'resolved', strategy: 'inline-row', value: { text: longSig, x: 0, y: 0 } }
      ];
      const block = buildDiagnosticsBlock(entries);
      const sigLine = block.split('\n').find((l) => l.includes('sig:'));

      expect(sigLine).toBeDefined();
      expect((sigLine as string).length).toBeLessThan(longSig.length);
    });
  });
});
