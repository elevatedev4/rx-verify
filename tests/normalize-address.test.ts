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

  describe('freeform (entered, single combined line) vs structured (source, split components)', () => {
    it('is GREEN when the entered single-line string matches the source components', () => {
      const r = compareAddresses(
        { street: '42 Fictional Wells Ct', city: 'Sampleville', state: 'KS', zip: '54321' },
        { street: '42 Fictional Wells Ct Sampleville, KS 54321' }
      );
      expect(r.status).toBe('green');
    });

    it('is still GREEN when the entered line omits the ZIP entirely (real PioneerRx display never shows one)', () => {
      const r = compareAddresses(
        { street: '42 Fictional Wells Ct', city: 'Sampleville', state: 'KS', zip: '54321' },
        { street: '42 Fictional Wells Ct Sampleville, KS' }
      );
      expect(r.status).toBe('green');
    });

    it('is YELLOW (never RED) when the entered single line is a genuinely different address', () => {
      const r = compareAddresses(
        { street: '42 Fictional Wells Ct', city: 'Sampleville', state: 'KS', zip: '54321' },
        { street: '999 Other St Topeka, KS 66601' }
      );
      expect(r.status).toBe('yellow');
      expect(r.status).not.toBe('red');
    });

    // Regression: a suite/unit entered INLINE on the freeform side (a
    // real, dump-confirmed PioneerRx shape — the prescriber-address
    // control routinely includes a suite inline in one combined string)
    // used to misalign every token after the unit against the structured
    // source, which already strips its unit out before comparing —
    // reading a genuinely identical address as a full mismatch.
    // Component-level extraction (parseFreeformAddress) fixes this by
    // stripping the unit out of the freeform side the same way.
    it('is GREEN when the same suite/unit is entered inline on the freeform side and as a separate component on the source side', () => {
      const r = compareAddresses(
        { street: '789 Fictional Blvd', unit: 'Ste B', city: 'Sampleburg', state: 'KS', zip: '11111' },
        { street: '789 Fictional Blvd Ste B Sampleburg, KS 11111' }
      );
      expect(r.status).toBe('green');
    });
  });

  describe('component-level matching (freeform entered line parsed into components, not whole-string fuzzy match)', () => {
    it('is GREEN for the same address in different formatting (abbreviation vs spelled out street type)', () => {
      const r = compareAddresses(
        { street: '123 Main Street', city: 'Testville', state: 'KS', zip: '99999' },
        { street: '123 Main St Testville, KS 99999' }
      );
      expect(r.status).toBe('green');
    });

    it('flags a mismatch when only the ZIP differs, even though street/city/state all agree', () => {
      const r = compareAddresses(
        { street: '123 Main St', city: 'Testville', state: 'KS', zip: '99999' },
        { street: '123 Main St Testville, KS 12345' }
      );
      // This engine's address comparator is TYPE-restricted to
      // green/yellow (see the "never returns RED" test above) — a
      // deliberate, pre-existing product decision ("address alone is
      // never RED — patients move"). A real, stated ZIP disagreement
      // must still be caught and flagged, just at this field's existing
      // "differs" severity (yellow), not upgraded to a severity this
      // field doesn't support.
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('flags a mismatch when only the city differs, even though street/state/zip all agree', () => {
      const r = compareAddresses(
        { street: '123 Main St', city: 'Testville', state: 'KS', zip: '99999' },
        { street: '123 Main St Springfield, KS 99999' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('does not mask a city/ZIP mismatch behind a coincidental token-count alignment', () => {
      // Same token COUNT on both sides, but city and zip both actually
      // differ — a naive whole-string positional comparison could only
      // ever get this right by luck; component-level parsing gets it
      // right by construction.
      const r = compareAddresses(
        { street: '123 Main St', city: 'Testville', state: 'KS', zip: '99999' },
        { street: '123 Main St Springfield, KS 12345' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('is NOT a hard mismatch when unit/apt is missing on one side only (downgraded to unit_differs, not address_differs)', () => {
      const r = compareAddresses(
        { street: '123 Main St', unit: 'Apt 4', city: 'Testville', state: 'KS', zip: '99999' },
        { street: '123 Main St Testville, KS 99999' } // no unit stated at all
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('unit_differs');
      expect(r.status).not.toBe('red');
    });
  });

  describe('missing street-type suffix on one side (W-T8 live-test bug)', () => {
    // Exact repro from Will's live-test report: source "330 Sycamore"
    // (no street type at all) vs entered "330 Sycamore St" was flagged
    // as no-match. A missing suffix on one side is an incomplete entry,
    // not a different street, and must read as a match.
    it('is GREEN for "330 Sycamore" vs "330 Sycamore St"', () => {
      const r = compareAddresses({ street: '330 Sycamore' }, { street: '330 Sycamore St' });
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('is GREEN with full city/state/zip present on both sides too', () => {
      const r = compareAddresses(
        { street: '330 Sycamore', city: 'Testville', state: 'KS', zip: '99999' },
        { street: '330 Sycamore St', city: 'Testville', state: 'KS', zip: '99999' }
      );
      expect(r.status).toBe('green');
    });

    it('is GREEN regardless of which side is missing the suffix', () => {
      const r = compareAddresses({ street: '42 Fictional Wells Ct' }, { street: '42 Fictional Wells' });
      expect(r.status).toBe('green');
    });

    it('still flags a genuine street-type disagreement when BOTH sides state a (different) suffix', () => {
      const r = compareAddresses({ street: '330 Sycamore St' }, { street: '330 Sycamore Ave' });
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('still catches a genuinely different street name even when one side lacks a suffix', () => {
      const r = compareAddresses({ street: '330 Sycamore' }, { street: '456 Oak St' });
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });
  });
});
