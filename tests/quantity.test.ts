import { describe, it, expect } from 'vitest';
import { compareQuantity, compareDaysSupply, compareRefills, comparePrescriber } from '../src/quantity/index.js';
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
});

describe('compareDaysSupply', () => {
  it('is YELLOW not_provided when source omits days supply (normal, optional field)', () => {
    const r = compareDaysSupply(undefined, 30);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is GREEN on exact match', () => {
    const r = compareDaysSupply(30, 30);
    expect(r.status).toBe('green');
  });

  it('is RED on mismatch', () => {
    const r = compareDaysSupply(30, 45);
    expect(r.status).toBe('red');
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
});

describe('comparePrescriber', () => {
  it('is GREEN exact_match on identical NPI and name', () => {
    const r = comparePrescriber({ name: 'Dr. Jane Doe', npi: '1234567890' }, { name: 'Dr. Jane Doe', npi: '1234567890' });
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is GREEN name_variant when NPI matches but name spelling differs', () => {
    const r = comparePrescriber({ name: 'Jonathan Reyes', npi: '1234567890' }, { name: 'Jon Reyes', npi: '1234567890' });
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('name_variant');
  });

  it('is RED on NPI mismatch', () => {
    const r = comparePrescriber({ name: 'Jane Doe', npi: '1234567890' }, { name: 'Jane Doe', npi: '9999999999' });
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('npi_mismatch');
  });

  it('falls back to name comparison when NPI absent on either side', () => {
    const r = comparePrescriber({ name: 'William Chen' }, { name: 'Bill Chen' });
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('nickname_match');
  });
});
