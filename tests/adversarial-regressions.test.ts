/**
 * Regression tests from the adversarial review (2026-07-04).
 * Each test uses the reviewer's exact reproducing input. The theme:
 * when the engine can't parse/compare something, the answer is YELLOW —
 * never a silent skip to green.
 */
import { describe, it, expect } from 'vitest';
import { compareSigs, parseSig } from '../src/sig/index.js';
import { compareNames } from '../src/normalize/name.js';
import { compareDates } from '../src/normalize/date.js';
import { compareQuantity } from '../src/quantity/index.js';
import { parseNdc, compareDrugs, FixtureProvider } from '../src/drug/index.js';

const provider = new FixtureProvider();

describe('BLOCKER 1: one-side-unparsed sig components must not skip to green', () => {
  it("reviewer repro: compareSigs('1 tab qod','1 tab qd') is not green", () => {
    const r = compareSigs('1 tab qod', '1 tab qd');
    expect(r.status).not.toBe('green');
    // qod is now a recognized frequency (0.5/day), so this is a proven
    // frequency contradiction: every-other-day vs daily.
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('unrecognized frequency-like token (q5h) makes the comparison indeterminate = yellow', () => {
    const r = compareSigs('1 tab q5h', '1 tab qd');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('sig_ambiguous');
  });

  it('frequency parsed on only one side = yellow sig_ambiguous, not green', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab po');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('sig_ambiguous');
  });

  it('route parsed on only one side = yellow sig_ambiguous, not green', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab bid');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('sig_ambiguous');
  });

  it('qod expansions: "every other day" and q.o.d. are recognized', () => {
    expect(compareSigs('1 tab po qod', '1 tab po every other day').status).toBe('green');
    expect(compareSigs('1 tab po q.o.d.', '1 tab po qod').status).toBe('green');
  });

  it('"every day" / "once a day" / "each day" are recognized as daily', () => {
    expect(compareSigs('1 tab po every day', '1 tab po qd').status).toBe('green');
    expect(compareSigs('1 tab po once a day', '1 tab po daily').status).toBe('green');
    expect(compareSigs('1 tab po each day', '1 tab po qd').status).toBe('green');
  });
});

describe('BLOCKER 2: duration must be compared', () => {
  it("reviewer repro: '1 tab po bid x7d' vs '1 tab po bid x30d' is RED", () => {
    const r = compareSigs('1 tab po bid x7d', '1 tab po bid x30d');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('duration on only one side = yellow', () => {
    const r = compareSigs('1 tab po bid x7d', '1 tab po bid');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('sig_ambiguous');
  });
});

describe('BLOCKER 3: dose unit must be compared', () => {
  it("reviewer repro: '1 tab' vs '1 cap' (same route/freq) is RED", () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 cap po bid');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('dose unit missing on one side carries no penalty', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 po bid');
    expect(r.status).toBe('green');
  });
});

describe('BLOCKER 4: partial compound-surname match is never green', () => {
  it("reviewer repro: 'Juan Garcia-Lopez' vs 'Juan Garcia' is YELLOW surname_partial", () => {
    const r = compareNames('Juan Garcia-Lopez', 'Juan Garcia');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('surname_partial');
  });

  it('full compound surname match is still green (hyphen vs space)', () => {
    const r = compareNames('Maria Garcia-Lopez', 'Maria Garcia Lopez');
    expect(r.status).toBe('green');
  });
});

describe('BLOCKER 5: quantity split reconciliation is bounded', () => {
  it('reviewer repro: qty 1 vs 1,000,000 is RED, not a plausible split', () => {
    const sig = parseSig('take 1 tab po qd');
    const r = compareQuantity(1, 'tab', 1000000, 'tab', sig);
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('quantity_mismatch');
  });

  it('implied days supply beyond 120 is not a plausible split', () => {
    const sig = parseSig('take 1 tab po qd');
    // 360 -> 360-day supply: outside plausibility bounds.
    const r = compareQuantity(360, 'tab', 30, 'tab', sig);
    expect(r.status).toBe('red');
  });

  it('non-integer split ratio is not a recognized split', () => {
    const sig = parseSig('take 1 tab po qd');
    // 45 days vs 30 days: ratio 1.5 — not a sane insurance split.
    const r = compareQuantity(45, 'tab', 30, 'tab', sig);
    expect(r.status).toBe('red');
  });

  it('the classic 90->30 split still reconciles as yellow', () => {
    const sig = parseSig('take 1 tab po qd');
    const r = compareQuantity(90, 'tab', 30, 'tab', sig);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('quantity_adjusted');
  });
});

describe('MAJOR 6: titles and generational suffixes', () => {
  it("'Dr. John Smith' vs 'John Smith' is GREEN", () => {
    const r = compareNames('Dr. John Smith', 'John Smith');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it("'John Smith Jr' vs 'John Smith' is YELLOW suffix_dropped (safer than green: a dropped Jr/Sr can mask a father/son swap)", () => {
    const r = compareNames('John Smith Jr', 'John Smith');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('suffix_dropped');
  });

  it("'John Smith Jr' vs 'John Smith Sr' is RED — same name, different person", () => {
    const r = compareNames('John Smith Jr', 'John Smith Sr');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('suffix_mismatch');
  });

  it('matching suffixes on both sides stay green', () => {
    const r = compareNames('John Smith Jr', 'Smith, John Jr');
    expect(r.status).toBe('green');
  });
});

describe('MAJOR 7: bare 10-digit NDC is ambiguous', () => {
  it('returns null for an undelimited 10-digit NDC instead of guessing 5-4-1', () => {
    expect(parseNdc('0071015523')).toBeNull();
  });

  it('dashed 10-digit NDCs still parse via dash-position disambiguation', () => {
    expect(parseNdc('0071-0155-23')?.normalized11).toBe('00071015523');
    expect(parseNdc('00071-155-23')?.normalized11).toBe('00071015523');
    expect(parseNdc('00071-0155-3')?.normalized11).toBe('00071015503');
  });

  it('comparison with a bare 10-digit NDC falls back to the name path', () => {
    const r = compareDrugs(
      { ndc: '0071015523', name: 'Zestril 10mg tablet' },
      { ndc: '00071015523' },
      provider
    );
    // Resolves both to the same concept via name/11-digit NDC; not red.
    expect(r.status).not.toBe('red');
  });
});

describe('MAJOR 8: FixtureProvider name lookup requires token-boundary match', () => {
  it("reviewer repro: '20mg tablet' resolves to nothing", () => {
    expect(provider.getConcept('20mg tablet')).toBeNull();
  });

  it('whole-name and ingredient-token lookups still work', () => {
    expect(provider.getConcept('Zestril 10mg tablet')?.rxcui).toBe('FX0001');
    expect(provider.getConcept('lisinopril 10mg tablet')?.ingredient).toBe('lisinopril');
  });
});

describe('MINOR 9: DOB two-digit-year future windowing', () => {
  it("reviewer repro: '3/5/45' vs '03/05/1945' as DOB is GREEN, not red", () => {
    const r = compareDates('3/5/45', '03/05/1945', { pastOnly: true, referenceYear: 2026 });
    expect(r.status).toBe('green');
  });

  it('non-DOB comparison keeps standard windowing', () => {
    const r = compareDates('3/5/45', '03/05/2045');
    expect(r.status).toBe('green');
  });
});

describe('MINOR 10: nickname collisions map to multiple canonicals', () => {
  it("'Al Johnson' matches 'Albert Johnson' as a nickname", () => {
    const r = compareNames('Al Johnson', 'Albert Johnson');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('nickname_match');
  });

  it("'Al Johnson' also matches 'Alexander Johnson' as a nickname", () => {
    const r = compareNames('Al Johnson', 'Alexander Johnson');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('nickname_match');
  });
});

describe('MINOR 11: prefix-tolerance explanation flags lower confidence', () => {
  it('prefix match explanation notes it is a lower-confidence match', () => {
    const r = compareNames('Christabel Okafor', 'Christa Okafor');
    expect(r.status).toBe('yellow');
    expect(r.explanation.toLowerCase()).toContain('lower-confidence');
  });
});

// --- Residuals from the re-verify pass (2026-07-04) ---

describe('RESIDUAL 1: hyphenated given names must not truncate to a false green', () => {
  it("reviewer repro: compareNames('Mary-Jane Smith','Mary Smith') is YELLOW given_name_partial", () => {
    const r = compareNames('Mary-Jane Smith', 'Mary Smith');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('given_name_partial');
  });

  it('full hyphenated given-name match is still green (hyphen vs space)', () => {
    const r = compareNames('Mary-Jane Smith', 'Mary Jane Smith');
    expect(r.status).toBe('green');
  });

  it('no shared given-name component with matching surname is still red', () => {
    const r = compareNames('Mary-Jane Smith', 'Peter Smith');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('first_name_mismatch');
  });
});

describe('RESIDUAL 2: stated-strength contradiction in free-text drug names', () => {
  it("reviewer repro: 'Lisinopril 20mg tablet' vs 'Lisinopril 10mg tablet' is RED, not generic_substitution", () => {
    const r = compareDrugs(
      { name: 'Lisinopril 20mg tablet' },
      { name: 'Lisinopril 10mg tablet' },
      provider
    );
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('drug_mismatch');
    expect(r.explanation).toContain('20mg');
    expect(r.explanation).toContain('10mg');
  });

  it('strength stated on only one name-resolved side = YELLOW strength_unverified, not a same-strength claim', () => {
    const r = compareDrugs({ name: 'Lisinopril tablet' }, { name: 'Lisinopril 10mg tablet' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('strength_unverified');
    expect(r.explanation.toLowerCase()).not.toContain('same ingredient, strength');
  });

  it('NDC-resolved side counts as strength-verified (no spurious unverified downgrade)', () => {
    // Source pinned by NDC (Zestril 10mg), entered stated as generic name with strength.
    const r = compareDrugs({ ndc: '00071015523' }, { name: 'Lisinopril 10mg tablet' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('generic_substitution');
  });
});

describe('RESIDUAL 3: middle initials get accurate wording, not compound-surname wording', () => {
  it("reviewer repro: 'John Q Smith' vs 'John Smith' is YELLOW middle_name_present", () => {
    const r = compareNames('John Q Smith', 'John Smith');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('middle_name_present');
    expect(r.explanation.toLowerCase()).toContain('middle name');
  });

  it('true compound-surname partials keep surname_partial', () => {
    const r = compareNames('Juan Garcia-Lopez', 'Juan Garcia');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('surname_partial');
  });
});
