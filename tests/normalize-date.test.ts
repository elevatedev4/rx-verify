import { describe, it, expect } from 'vitest';
import { parseDate, compareDates } from '../src/normalize/date.js';

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
