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
