import { describe, it, expect } from 'vitest';
import {
  compareQuantity,
  compareRefills,
  comparePrescriberName,
  comparePrescriberNpi,
  comparePrescriberPhone,
  comparePrescriberAddress
} from '../src/quantity/index.js';
import { parseSig } from '../src/sig/index.js';

describe('compareQuantity', () => {
  it('is GREEN on exact match', () => {
    const r = compareQuantity(60, 'tab', 60, 'tablets', null);
    expect(r.status).toBe('green');
  });

  it('is YELLOW quantity_adjusted for a 90->30 insurance split reconciled by sig math', () => {
    // sig: 1 tab bid = 2/day. 90 -> 45 days, 30 -> 15 days. Both whole.
    const sig = parseSig('take 1 tab po bid');
    const r = compareQuantity(90, 'tab', 30, 'tab', sig);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('quantity_adjusted');
  });

  it('is RED when quantity differs and does not reconcile with sig math', () => {
    const sig = parseSig('take 1 tab po bid');
    const r = compareQuantity(60, 'tab', 47, 'tab', sig);
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('quantity_mismatch');
  });

  it('is YELLOW not_provided when source missing', () => {
    const r = compareQuantity(undefined, undefined, 30, 'tab', null);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  // Repro for Will's live-test bug report: an Rx with quantity "90" on
  // BOTH sides flagged a RED mismatch. Root cause: the e-script's
  // QuantityUnitOfMeasure is frequently the NCPDP code "C38046
  // (Unspecified)" (see overlay EscriptTreeParser.ParseQuantityUnit,
  // which extracts just the parenthetical -> "Unspecified"), while the
  // entered side's unit comes from PioneerRx's own unit ComboBox (e.g.
  // "ML", "EA"). "Unspecified" was being unit-normalized like any other
  // real unit and then compared literally against "ml"/"ea" — since
  // "unspecified" !== "ml", unitsCompatible was false and the whole
  // comparison short-circuited to RED unit_mismatch even though the
  // numeric quantities were IDENTICAL. "Unspecified" isn't a real,
  // conflicting unit — it's the source explicitly saying it didn't
  // specify one, so it must be treated as "not provided" for unit
  // comparison purposes (no unit-compatibility check at all), not as a
  // hard mismatch.
  it('is GREEN on 90 vs 90 when the source unit is the NCPDP "Unspecified" placeholder', () => {
    const r = compareQuantity(90, 'Unspecified', 90, 'ML', null);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is GREEN on 90 vs 90 when the source unit is null/empty and only the entered side has a real unit', () => {
    const r = compareQuantity(90, null, 90, 'EA', null);
    expect(r.status).toBe('green');
  });

  // Repro for the SECOND live-test bug (W-T8): "24" vs "24" still flagged
  // RED even after the "Unspecified" fold fix. Root cause: source unit
  // "Tablet" (a real, specific NCPDP unit, not the "Unspecified"
  // placeholder) folds to "tab", while PioneerRx's entered unit ComboBox
  // held "EA" (confirmed real value in overlay Uia/FieldMap.cs) — a
  // generic per-unit designation techs commonly leave/select for
  // countable solid dosage forms regardless of the e-script's stated
  // unit word. "tab" !== "ea" so unit-compatibility failed and the whole
  // field went RED unit_mismatch even with numerically identical
  // quantities. The overlay's quantity column shows only the bare
  // number (no unit), so this looked like an unexplained "24 vs 24"
  // false mismatch.
  it('is GREEN on 24 vs 24 when the source unit is a specific unit ("Tablet") and the entered unit is PioneerRx\'s generic "EA"', () => {
    const r = compareQuantity(24, 'Tablet', 24, 'EA', null);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is GREEN on 24 vs 24 when the entered unit is "Each" (spelled out) and the source unit is a specific unit', () => {
    const r = compareQuantity(24, 'Capsule', 24, 'Each', null);
    expect(r.status).toBe('green');
  });

  it('is GREEN on 24 vs 24 with no units stated on either side', () => {
    const r = compareQuantity(24, null, 24, null, null);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  // Repro for Will's live-test bug report: source "42.5000 Grams" vs
  // entered 42.5 "gm" flagged a false RED unit_mismatch ("g" vs "gm")
  // even though the numeric quantity matched exactly. "gm" is a real
  // gram spelling (confirmed in the same live-test session where "12.0000"
  // vs "12" compared GREEN) that was simply missing from UNIT_ALIASES.
  it('is GREEN on "42.5000 Grams" vs 42.5 "gm" (live-test bug repro)', () => {
    const r = compareQuantity('42.5000', 'Grams', 42.5, 'gm', null);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('treats every gram spelling (g / gm / gram / grams) as the same unit', () => {
    const spellings = ['g', 'gm', 'gram', 'grams', 'GM', 'Gram', 'GRAMS'];
    for (const unit of spellings) {
      const r = compareQuantity(10, 'g', 10, unit, null);
      expect(r.status, `unit "${unit}" should match "g"`).toBe('green');
    }
  });

  it('treats mcg / microgram / micrograms as the same unit', () => {
    const r1 = compareQuantity(100, 'mcg', 100, 'microgram', null);
    expect(r1.status).toBe('green');
    const r2 = compareQuantity(100, 'Micrograms', 100, 'MCG', null);
    expect(r2.status).toBe('green');
  });

  it('does NOT treat "g" and "kg" as the same unit (a real conversion, not a spelling synonym) — still RED', () => {
    const r = compareQuantity(1, 'kg', 1000, 'g', null);
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('unit_mismatch');
  });
});

describe('compareRefills', () => {
  it('is GREEN on exact match', () => {
    expect(compareRefills(2, 2).status).toBe('green');
  });

  it('is RED on any mismatch', () => {
    expect(compareRefills(2, 3).status).toBe('red');
  });

  it('is YELLOW not_provided when missing', () => {
    expect(compareRefills(undefined, 2).status).toBe('yellow');
  });

  describe('sourceIsTotalFills (Change 3 — "Total fills: N" means N-1 refills)', () => {
    it('GREEN: source "Total fills: 5", entered 4', () => {
      const r = compareRefills(5, 4, true);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('GREEN: source "Total fills: 1", entered 0', () => {
      const r = compareRefills(1, 0, true);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('GREEN (unchanged): plain "Refills: 4", entered 4 — no subtraction without the flag', () => {
      const r = compareRefills(4, 4);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('RED: source "Total fills: 5", entered 5 — effective is 4, so 5 mismatches', () => {
      const r = compareRefills(5, 5, true);
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('refills_mismatch');
    });

    it('explanation surfaces the derived effective count so the raw source number is not confusing on its own', () => {
      const r = compareRefills(5, 4, true);
      expect(r.explanation).toMatch(/total fills.*5/i);
      expect(r.explanation).toMatch(/4/);
    });

    it('GREEN, clamped at 0: source "Total fills: 0", entered 0 — never goes to -1', () => {
      const r = compareRefills(0, 0, true);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('RED, clamped at 0 (not -1): source "Total fills: 0", entered 1 does not somehow read as "closer" via a negative effective count', () => {
      const r = compareRefills(0, 1, true);
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('refills_mismatch');
    });
  });
});

describe('comparePrescriberName', () => {
  it('is GREEN exact_match on identical name (module 1 rules, independent of NPI)', () => {
    const r = comparePrescriberName('Dr. Jane Doe', 'Dr. Jane Doe');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is YELLOW nickname_match on a spelling/nickname variant, even when NPI (checked separately) matches', () => {
    // Previously a matching NPI forced the bundled prescriber field to
    // GREEN "name_variant" even though the name text differed. Now that
    // name and NPI are separate fields, the name field is judged purely
    // on name rules — the NPI field alongside it is what carries green.
    const r = comparePrescriberName('Jonathan Reyes', 'Jon Reyes');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('nickname_match');
  });

  it('is RED on surname mismatch', () => {
    const r = comparePrescriberName('Jane Doe', 'Jane Smith');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('surname_mismatch');
  });
});

describe('comparePrescriberNpi', () => {
  it('is GREEN on identical NPI', () => {
    const r = comparePrescriberNpi('1234567890', '1234567890');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is RED on NPI mismatch', () => {
    const r = comparePrescriberNpi('1234567890', '9999999999');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('npi_mismatch');
  });

  it('is YELLOW not_provided when either side is missing', () => {
    expect(comparePrescriberNpi(undefined, '1234567890').status).toBe('yellow');
    expect(comparePrescriberNpi('1234567890', undefined).status).toBe('yellow');
  });
});

describe('comparePrescriberPhone', () => {
  it('is GREEN on identical phone regardless of formatting', () => {
    const r = comparePrescriberPhone('(555) 200-1000', '5552001000');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is GREEN when a leading US country code differs but the last 10 digits match', () => {
    const r = comparePrescriberPhone('15552001000', '(555) 200-1000');
    expect(r.status).toBe('green');
  });

  it('is YELLOW (never RED) on a differing phone number', () => {
    const r = comparePrescriberPhone('5552001000', '5559998888');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('phone_differs');
  });

  it('is YELLOW not_provided when either side is missing', () => {
    expect(comparePrescriberPhone(undefined, '5552001000').status).toBe('yellow');
  });

  // Bug 1 (round 3, W-T-round3): OCR dropped/garbled the area code's
  // leading digit ("(7" misread as "0") so the source phone's area-code
  // digits are structurally impossible (a real NANP area code never
  // starts with 0 or 1) while the local 7 digits still match exactly.
  // That's OCR noise, not a genuinely different number — downgrade to a
  // distinct, pharmacist-readable YELLOW reason instead of the generic
  // "phone_differs" wording, so the UI can point straight at "go look
  // closer, this is probably just an OCR misread" rather than "this
  // could be a second legitimate clinic line".
  it('is YELLOW phone_ocr_suspect when the local 7 digits match but one side\'s area code is structurally invalid (OCR-garbled)', () => {
    const r = comparePrescriberPhone('085) 555-0142', '(785) 555-0142');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('phone_ocr_suspect');
    expect(r.explanation).toMatch(/area code/i);
  });

  // Two fully valid, structurally normal 10-digit numbers whose ONLY
  // difference is the area code (local 7 digits match) are NOT OCR noise
  // — a real area-code-only difference like this is a much more
  // suspicious pattern than "a clinic has more than one legit line"
  // (that pattern usually differs in the local number too), so this
  // stays RED rather than falling under the "phone alone is never RED"
  // multi-line-clinic leniency.
  it('is RED when two structurally valid 10-digit numbers have genuinely different area codes (same local number)', () => {
    const r = comparePrescriberPhone('(913) 555-0142', '(785) 555-0142');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('phone_area_code_mismatch');
  });

  it('is still YELLOW (never RED) when the local 7 digits themselves differ, even with matching area codes', () => {
    // Same as the existing "never RED on a differing phone number" case
    // above (555-200-1000 vs 555-999-8888) — pinned again here
    // explicitly alongside the new RED case so the boundary between them
    // is obvious from reading the tests together.
    const r = comparePrescriberPhone('(555) 200-1000', '(555) 999-8888');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('phone_differs');
  });
});

describe('comparePrescriberAddress', () => {
  it('is GREEN when the source (split components) matches the entered (single combined line)', () => {
    const r = comparePrescriberAddress(
      { street: '1 Clinic Way', city: 'Sampleville', state: 'KS', zip: '99887' },
      { street: '1 Clinic Way Sampleville, KS 99887' }
    );
    expect(r.status).toBe('green');
  });

  it('is YELLOW (never RED) on a differing address', () => {
    const r = comparePrescriberAddress(
      { street: '1 Clinic Way', city: 'Sampleville', state: 'KS', zip: '99887' },
      { street: '999 Other St Topeka, KS 66601' }
    );
    expect(r.status).toBe('yellow');
    expect(r.status).not.toBe('red');
  });
});
