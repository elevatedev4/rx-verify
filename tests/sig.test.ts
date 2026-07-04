import { describe, it, expect } from 'vitest';
import { parseSig, compareSigs } from '../src/sig/index.js';

describe('parseSig', () => {
  it('extracts dose count, route, frequency from abbreviated sig', () => {
    const p = parseSig('take 1 tab po bid');
    expect(p.doseCount).toBe(1);
    expect(p.route).toBe('po');
    expect(p.timesPerDay).toBe(2);
    expect(p.ambiguous).toBe(false);
  });

  it('extracts roman numeral dose counts', () => {
    const p = parseSig('take ii tabs po tid');
    expect(p.doseCount).toBe(2);
    expect(p.timesPerDay).toBe(3);
  });

  it('extracts PRN flag', () => {
    const p = parseSig('take 1 tab po q4h prn');
    expect(p.prn).toBe(true);
  });

  it('extracts duration in days', () => {
    const p = parseSig('take 1 cap po bid x10d');
    expect(p.durationDays).toBe(10);
  });

  it('extracts expanded multi-word equivalents identically to abbreviations', () => {
    const p1 = parseSig('take 1 tablet by mouth twice daily');
    const p2 = parseSig('take 1 tab po bid');
    expect(p1.doseCount).toBe(p2.doseCount);
    expect(p1.route).toBe(p2.route);
    expect(p1.timesPerDay).toBe(p2.timesPerDay);
  });

  it('marks a sig with no extractable structure as ambiguous', () => {
    const p = parseSig('use as directed');
    expect(p.ambiguous).toBe(true);
  });
});

describe('compareSigs', () => {
  it('is GREEN when expansions are semantically equal', () => {
    const r = compareSigs('take 1 tablet by mouth twice daily', 'take 1 tab po bid');
    expect(r.status).toBe('green');
  });

  it('is GREEN for q.d./daily variants', () => {
    const r = compareSigs('take 1 tab po q.d.', 'take 1 tab po daily');
    expect(r.status).toBe('green');
  });

  it('is RED on dose count mismatch', () => {
    const r = compareSigs('take 1 tab po bid', 'take 2 tab po bid');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('is RED on route mismatch', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab top bid');
    expect(r.status).toBe('red');
  });

  it('is RED on frequency mismatch', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab po tid');
    expect(r.status).toBe('red');
  });

  it('is YELLOW sig_ambiguous when either side is unparseable', () => {
    const r = compareSigs('use as directed', 'take 1 tab po bid');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('sig_ambiguous');
  });

  it('is YELLOW not_provided when source is missing', () => {
    const r = compareSigs(undefined, 'take 1 tab po bid');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });
});
