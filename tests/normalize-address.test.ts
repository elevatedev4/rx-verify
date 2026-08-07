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

  it('compares only the first 5 digits of ZIP (W-T10 item 1): dashed +4, undashed 9-digit, and bare 5-digit all match "66047"', () => {
    const dashed = compareAddresses(
      { street: '123 Main St', city: 'Lawrence', state: 'KS', zip: '66047-1234' },
      { street: '123 Main St', city: 'Lawrence', state: 'KS', zip: '66047' }
    );
    expect(dashed.status).toBe('green');

    const undashed = compareAddresses(
      { street: '123 Main St', city: 'Lawrence', state: 'KS', zip: '660471234' },
      { street: '123 Main St', city: 'Lawrence', state: 'KS', zip: '66047' }
    );
    expect(undashed.status).toBe('green');
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

  describe('glued OCR tokens (live-test bug: "suite205" vs "Suite 205")', () => {
    it('is GREEN when the source has a glued letter+digit unit token and the entered side has it split', () => {
      const r = compareAddresses(
        { street: '330 Arkansas Blvd suite205', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '330 Arkansas Blvd Suite 205', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('is GREEN for the exact live-test freeform repro: "...suite205..." vs "...Suite 205..."', () => {
      const r = compareAddresses(
        { street: '330 Arkansas Blvd suite205 Testville, KS 66049' },
        { street: '330 Arkansas Blvd Suite 205 Testville, KS 66049' }
      );
      expect(r.status).toBe('green');
    });

    it('splits "apt3b" the same way ("apt 3b")', () => {
      const r = compareAddresses(
        { street: '42 Fictional Wells Ct apt3b', city: 'Sampleville', state: 'KS', zip: '54321' },
        { street: '42 Fictional Wells Ct Apt 3b', city: 'Sampleville', state: 'KS', zip: '54321' }
      );
      expect(r.status).toBe('green');
    });
  });

  describe('single-character street-name OCR misreads (live-test bug: "overtand" vs "Overland")', () => {
    it('is GREEN for "4930 overtand Drive" vs "4930 Overland Dr" (1-edit street-name tolerance)', () => {
      const r = compareAddresses(
        { street: '4930 overtand Drive', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4930 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('is GREEN for the exact live-test freeform repro', () => {
      const r = compareAddresses(
        { street: '4930 overtand Drive Testville, KS 66049' },
        { street: '4930 Overland Dr Testville, KS 66049' }
      );
      expect(r.status).toBe('green');
    });

    it('SAFETY BOUND: house number is never fuzzed — "4930" vs "4931" still differs', () => {
      const r = compareAddresses(
        { street: '4930 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4931 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('SAFETY BOUND: state code is never fuzzed — "KS" vs "MO" still differs even with an otherwise-identical street', () => {
      const r = compareAddresses(
        { street: '4930 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4930 Overland Dr', city: 'Testville', state: 'MO', zip: '66049' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('does not fuzz short (<5 char) alphabetic tokens — a genuinely different short street name still differs', () => {
      const r = compareAddresses({ street: '10 Elm St' }, { street: '10 Elk St' });
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('does not fuzz two street names that are >1 edit apart', () => {
      const r = compareAddresses({ street: '100 Fictional Rd' }, { street: '100 Completely Rd' });
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    // BLOCKER FIX (reviewer round 1): the initial <=1-edit-distance
    // tolerance had no length constraint, so it also matched an
    // INSERTION/DELETION away, not just a same-length substitution —
    // verified by the reviewer to produce a false GREEN on real,
    // realistic street-name collisions ("Meadow"/"Meadows",
    // "Wilson"/"Wilton"-shaped pairs are both common). Restricting the
    // tolerance to SAME-LENGTH tokens closes the insertion/deletion class
    // entirely (an edit distance of 1 between equal-length strings can
    // only ever be a single-character substitution) while still fixing
    // the originally reported bug ("overtand"/"Overland" — same length,
    // one substitution). See streetTokensMatch's doc comment for the full
    // invariant + residual-risk writeup.
    it('does NOT fuzz an insertion/deletion — "Meadow" vs "Meadows" (different length) still differs', () => {
      const r = compareAddresses(
        { street: '100 Meadow Ln', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '100 Meadows Ln', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('does NOT fuzz an insertion/deletion — "Parkway" vs "Parkways" (different length) still differs', () => {
      const r = compareAddresses({ street: '200 Fictional Parkway' }, { street: '200 Fictional Parkways' });
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    // KNOWINGLY ACCEPTED TRADEOFF (documented, not a bug): a same-length,
    // one-substitution collision between two DIFFERENT real street names
    // ("Wilson" vs "Wilton") still matches under this tolerance — a
    // substitution-only rule can't distinguish that shape from the
    // "overtand"/"Overland" OCR-misread shape it exists to fix; they're
    // the same edit. This is accepted rather than closed because: (1)
    // address is a yellow-tier field that never blocks dispensing
    // ("patients move" — see this file's top-of-module verdict
    // philosophy) and never carries patient identity on its own (name +
    // DOB do); (2) the alternative — zero street-name tolerance at all —
    // regenerates the exact daily false-yellow alarm fatigue the
    // "overtand"/"Overland" live-test bug this fix exists to stop. If
    // this test ever needs to flip to yellow (i.e. the tolerance gets
    // tightened further), that's a deliberate product decision to make,
    // not an accidental regression — update this comment alongside it.
    it('ACCEPTED TRADEOFF: same-length 1-substitution collision between two different real street names still matches — "Wilson" vs "Wilton"', () => {
      const r = compareAddresses(
        { street: '400 Wilson St', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '400 Wilton St', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });
  });

  describe('source address stops at the city — no state/ZIP at all (defect #7c, live-test bug: OCR read "808 E 1250 Road Lawrence", never reached state/ZIP, and compared address_differs against a fully-populated entered address)', () => {
    // Acceptance (1): confirmed street+city, source simply never provided
    // state/ZIP → GREEN, with an explanation that says what was (and
    // wasn't) actually compared.
    it('is GREEN when source has street+city but no state/ZIP at all, and both match the entered address', () => {
      const r = compareAddresses(
        { street: '907 W 1300 Road', city: 'Faketown' },
        { street: '907 W 1300 Road', city: 'Faketown', state: 'KS', zip: '66099' }
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match_partial_source');
      expect(r.explanation).toContain('Street and city match');
      expect(r.explanation).toContain('state/ZIP');
    });

    it('explanation names ONLY the missing piece when just one of state/ZIP is absent (state present-and-matching, ZIP absent)', () => {
      const r = compareAddresses(
        { street: '907 W 1300 Road', city: 'Faketown', state: 'KS' },
        { street: '907 W 1300 Road', city: 'Faketown', state: 'KS', zip: '66099' }
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match_partial_source');
      expect(r.explanation).toContain('did not provide ZIP');
      expect(r.explanation).not.toContain('state/ZIP');
    });

    // Acceptance (2): same shape, but the entered CITY genuinely differs
    // — must NOT be waved through by the new state/ZIP leniency.
    it('is YELLOW when source has no state/ZIP AND the entered city differs', () => {
      const r = compareAddresses(
        { street: '907 W 1300 Road', city: 'Faketown' },
        { street: '907 W 1300 Road', city: 'Differentburg', state: 'KS', zip: '66099' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    // SAFETY BOUND (i): a present-but-DIFFERENT state must still flag —
    // this is not the same thing as "source didn't provide a state".
    it('SAFETY BOUND: is YELLOW when source STATES a different state, even though ZIP is absent on the source', () => {
      const r = compareAddresses(
        { street: '907 W 1300 Road', city: 'Faketown', state: 'MO' },
        { street: '907 W 1300 Road', city: 'Faketown', state: 'KS', zip: '66099' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    // Acceptance (5) / SAFETY BOUND (ii): source missing CITY too
    // (street-only) is "too little to verify" — must stay yellow, never
    // waved through by this leniency (which requires a CONFIRMED city
    // match, not just "city wasn't stated").
    it('SAFETY BOUND: street-only source (no city, no state, no ZIP) stays YELLOW, not green', () => {
      const r = compareAddresses(
        { street: '907 W 1300 Road' },
        { street: '907 W 1300 Road', city: 'Faketown', state: 'KS', zip: '66099' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    // SAFETY BOUND (iii): the new leniency (reasonCode
    // 'exact_match_partial_source') is state/ZIP-only and requires an
    // actively CONFIRMED street match — a source missing STREET entirely
    // must not produce that reasonCode. UPGRADED (round-2 review fold
    // #2): this used to also silently fall through to GREEN via a
    // separate, unrelated general blank-component tolerance that treated
    // a blank source street the same as a blank entered street — a real
    // gap (a source with no street confirms literally nothing about the
    // street), now closed by streetDiffers' own sourceAbsentIsGap check
    // (mirrors cityMissingOrDiffers). This asserts the full outcome, not
    // just the reasonCode.
    it('SAFETY BOUND: the new partial-source leniency does not fire when street itself is absent on the source, and the overall result is YELLOW (not a silent green)', () => {
      const r = compareAddresses(
        { city: 'Faketown' },
        { street: '907 W 1300 Road', city: 'Faketown', state: 'KS', zip: '66099' }
      );
      expect(r.reasonCode).not.toBe('exact_match_partial_source');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });
  });

  // Bug 2 (round 3, W-T-round3): live report — source street suffix read
  // as "m" (OCR mangled "Dr"), landing address_differs even though the
  // rest of the street (and the whole rest of the address) matched.
  // normalizeStreetLine already treats a MISSING suffix as a non-
  // mismatch (see "330 Sycamore" vs "330 Sycamore St" above); this
  // extends that to a PRESENT-BUT-GARBAGE suffix token: a short (1-2
  // char), unrecognized, non-directional trailing token on the side that
  // has no real suffix, when the OTHER side states an actual recognized
  // suffix, is dropped as OCR noise so the streets compare on their
  // cores.
  describe('garbage short suffix token where a real street suffix should be (live-test bug: "m" for "Dr")', () => {
    it('is GREEN for "4930 Overland m" vs "4930 Overland Dr" (garbage 1-char suffix dropped as OCR noise)', () => {
      const r = compareAddresses(
        { street: '4930 Overland m', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4930 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('is GREEN for the live-test shape (house number + street name + garbled 1-char suffix + inline unit): "1811 Fictional m ste 102" vs "1811 Fictional Dr Ste 102"', () => {
      const r = compareAddresses(
        { street: '1811 Fictional m ste 102', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '1811 Fictional Dr Ste 102', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('exact_match');
    });

    it('SAFETY BOUND: a recognized-but-DIFFERENT suffix on both sides ("St" vs "Dr") still differs — never dropped as noise', () => {
      const r = compareAddresses(
        { street: '4930 Overland St', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4930 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });

    it('SAFETY BOUND: a garbage-shaped trailing token is only dropped when the OTHER side has a real recognized suffix — two garbage/absent suffixes still compare on the raw core', () => {
      const r = compareAddresses(
        { street: '4930 Overland m', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4931 Overland', city: 'Testville', state: 'KS', zip: '66049' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });
  });

  // Bug 4 (round 3, W-T-round3): live report — prescriber addresses
  // otherwise identical, but the ENTERED (freeform, PioneerRx) side
  // lacked a comma before the state ("...Testville KS", no comma) so
  // parseFreeformAddress's ", ST [ZIP]" anchor never matched at all and
  // the WHOLE remaining line (city+state included) fell into `street`
  // undifferentiated — misaligning the street-core token count against
  // the source's cleanly split components and landing address_differs,
  // even though every component actually agreed. The source-side parser
  // (ADDRESS_RE in parseEscriptOcr.ts) already tolerates a comma-OR-
  // whitespace separator before the state; parseFreeformAddress required
  // a literal comma — that asymmetry is the actual bug. A component
  // absent on EITHER side (entered's freeform line omitting the ZIP
  // entirely, the pattern actually named in the report) was already a
  // non-disagreement via componentDiffers; the real gap was upstream, in
  // recognizing the state/city split at all when there's no comma.
  describe('freeform entered line with no comma before the state (live-test bug: real component match landing address_differs)', () => {
    it('is GREEN when the entered freeform line has no comma before the state and omits the ZIP entirely', () => {
      const r = compareAddresses(
        { street: '4930 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4930 Overland Dr Testville KS' }
      );
      expect(r.status).toBe('green');
    });

    it('is GREEN (pinned regression) for the comma-present shape from the branch brief: entered line missing only the ZIP', () => {
      const r = compareAddresses(
        { street: '4930 Overland Dr Testville, KS 66049' },
        { street: '4930 Overland Dr Testville, KS' }
      );
      expect(r.status).toBe('green');
    });

    it('SAFETY BOUND: a no-comma entered line with a genuinely DIFFERENT state still differs', () => {
      const r = compareAddresses(
        { street: '4930 Overland Dr', city: 'Testville', state: 'KS', zip: '66049' },
        { street: '4930 Overland Dr Testville MO' }
      );
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('address_differs');
    });
  });
});
