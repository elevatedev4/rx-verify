import { describe, it, expect } from 'vitest';
import { parseDate, compareDates, compareWrittenOrAvailableDate } from '../src/normalize/date.js';

describe('parseDate', () => {
  it('parses MM/DD/YYYY', () => {
    expect(parseDate('07/02/2026')).toBe('2026-07-02');
  });

  it('parses M/D/YY with 2000s window', () => {
    expect(parseDate('7/2/26')).toBe('2026-07-02');
  });

  it('parses M/D/YY with 1900s window', () => {
    expect(parseDate('7/2/65')).toBe('1965-07-02');
  });

  it('parses ISO YYYY-MM-DD', () => {
    expect(parseDate('2026-07-02')).toBe('2026-07-02');
  });

  it('parses "Jul 2, 2026"', () => {
    expect(parseDate('Jul 2, 2026')).toBe('2026-07-02');
  });

  it('parses "July 2, 2026"', () => {
    expect(parseDate('July 2, 2026')).toBe('2026-07-02');
  });

  it('returns null for garbage input', () => {
    expect(parseDate('not a date')).toBeNull();
  });

  it('returns null for invalid day', () => {
    expect(parseDate('02/30/2026')).toBeNull();
  });
});

describe('compareDates', () => {
  it('is GREEN on exact match across formats', () => {
    const r = compareDates('07/02/2026', 'Jul 2, 2026');
    expect(r.status).toBe('green');
  });

  it('is RED when both present and differ', () => {
    const r = compareDates('07/02/2026', '07/03/2026');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('date_mismatch');
  });

  it('is YELLOW not_provided when source absent', () => {
    const r = compareDates(undefined, '07/02/2026');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is YELLOW not_provided when entered absent', () => {
    const r = compareDates('07/02/2026', '');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is YELLOW unparseable_date when a date cannot be parsed', () => {
    const r = compareDates('07/02/2026', 'whenever');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unparseable_date');
  });
});

// Round 5, fix 3 (live report): some e-scripts show an "Available:" date;
// PioneerRx then displays THAT date, not the Written date, in its own
// entered fields — so the entered date is expected to match Available
// when the source has one, not Written.
describe('compareWrittenOrAvailableDate', () => {
  it('ACCEPTANCE: source shows an Available date and no Written date; entered matches Available -> GREEN (previously yellow not_provided)', () => {
    const r = compareWrittenOrAvailableDate(undefined, '07/19/2026', '7/19/2026');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('available_date_match');
  });

  it('no availableDate at all -> behaves exactly like plain compareDates against Written (unchanged)', () => {
    const withAvailable = compareWrittenOrAvailableDate('07/02/2026', undefined, '07/02/2026');
    const plain = compareDates('07/02/2026', '07/02/2026');
    expect(withAvailable).toEqual(plain);
  });

  it('no availableDate, Written also absent -> still the ordinary not_provided yellow (unchanged)', () => {
    const r = compareWrittenOrAvailableDate(undefined, undefined, '07/02/2026');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('both Written and Available present; entered matches Available -> GREEN available_date_match (Available wins even though Written also present)', () => {
    const r = compareWrittenOrAvailableDate('07/01/2026', '07/19/2026', '07/19/2026');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('available_date_match');
  });

  it('both present, entered matches Written but not Available -> falls back to the ordinary Written match (still GREEN, exact_match)', () => {
    const r = compareWrittenOrAvailableDate('07/01/2026', '07/19/2026', '07/01/2026');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('both present, entered matches NEITHER -> explicit RED date_mismatch', () => {
    const r = compareWrittenOrAvailableDate('07/01/2026', '07/19/2026', '08/15/2026');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('date_mismatch');
  });

  it('Available present, no Written, entered does not match Available -> surfaces the ordinary Available-vs-entered mismatch (RED date_mismatch), not a fabricated yellow', () => {
    const r = compareWrittenOrAvailableDate(undefined, '07/19/2026', '08/15/2026');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('date_mismatch');
  });

  it('Available present, no entered date -> ordinary not_provided yellow (entered side is the gap, not a fabricated mismatch)', () => {
    const r = compareWrittenOrAvailableDate(undefined, '07/19/2026', undefined);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('Available present but unparseable, entered present -> yellow unparseable_date, not a RED mismatch', () => {
    const r = compareWrittenOrAvailableDate(undefined, 'not a date', '07/19/2026');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unparseable_date');
  });
});
