import { describe, it, expect } from 'vitest';
import { compareAddresses } from '../src/normalize/address.js';

describe('compareAddresses', () => {
  it('is GREEN on exact match after suffix/directional normalization', () => {
    const r = compareAddresses(
      { street: '123 North Main Street', city: 'Springfield', state: 'IL', zip: '62704' },
      { street: '123 N Main St', city: 'Springfield', state: 'IL', zip: '62704' }
    );
    expect(r.status).toBe('green');
  });

  it('treats ZIP+4 vs ZIP5 as equal', () => {
    const r = compareAddresses(
      { street: '123 Main St', city: 'Springfield', state: 'IL', zip: '62704-1234' },
      { street: '123 Main St', city: 'Springfield', state: 'IL', zip: '62704' }
    );
    expect(r.status).toBe('green');
  });

  it('is YELLOW unit_differs on unit-only difference', () => {
    const r = compareAddresses(
      { street: '123 Main St Apt 4', city: 'Springfield', state: 'IL', zip: '62704' },
      { street: '123 Main St Apt 5', city: 'Springfield', state: 'IL', zip: '62704' }
    );
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unit_differs');
  });

  it('is YELLOW address_differs on different street, never RED', () => {
    const r = compareAddresses(
      { street: '123 Main St', city: 'Springfield', state: 'IL', zip: '62704' },
      { street: '456 Oak Ave', city: 'Shelbyville', state: 'IL', zip: '62705' }
    );
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('address_differs');
  });

  it('is YELLOW not_provided when source is missing', () => {
    const r = compareAddresses(undefined, { street: '123 Main St', city: 'Springfield', state: 'IL', zip: '62704' });
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('never returns RED', () => {
    const cases: Array<[any, any]> = [
      [undefined, { street: '1 A St' }],
      [{ street: '1 A St', city: 'X', state: 'IL', zip: '11111' }, { street: '2 B Ave', city: 'Y', state: 'CA', zip: '22222' }]
    ];
    for (const [a, b] of cases) {
      expect(compareAddresses(a, b).status).not.toBe('red');
    }
  });
});
