import { describe, it, expect } from 'vitest';
import { verify, PENDING_DRUG_LOOKUP_REASON_CODE } from '../src/engine/index.js';
import { FixtureProvider } from '../src/drug/index.js';
import { FIELD_ORDER } from '../src/types.js';

const provider = new FixtureProvider();

describe('verify engine', () => {
  it('always returns verdicts in FIELD_ORDER (minus the conditional availableDate slot, absent here since source has none)', () => {
    const result = verify({}, {}, provider);
    // Round 5 fix 3: 'availableDate' is a CONDITIONAL slot in FIELD_ORDER
    // (only rendered when source.availableDate is set — see its doc,
    // types.ts) — every other field is unconditional, so the remaining
    // 13 still appear in FIELD_ORDER's exact relative order.
    expect(result.verdicts.map((v) => v.field)).toEqual(FIELD_ORDER.filter((f) => f !== 'availableDate'));
  });

  it('renders the conditional availableDate verdict, in FIELD_ORDER position, only when source.availableDate is set', () => {
    const result = verify({ availableDate: '07/19/2026' }, {}, provider);
    expect(result.verdicts.map((v) => v.field)).toEqual([...FIELD_ORDER]);
  });

  // NON-BLOCKING HARDENING (reviewer, round 5 fix 3 follow-up): the
  // order check alone (a monotonic-subsequence walk over FIELD_ORDER) no
  // longer catches a mandatory field silently DROPPED from the verdicts
  // array — a shorter subsequence is still a valid subsequence. verify()
  // now also asserts verdicts.length === FIELD_ORDER.length minus
  // exactly the conditional fields absent (today: just 'availableDate').
  // These two pin that exact completeness invariant directly, for both
  // states of the one existing conditional field — a fail-fast guard
  // that would throw immediately if a future edit ever dropped e.g.
  // 'quantity' from the array literal in engine/index.ts.
  describe('completeness assertion (verdicts.length matches FIELD_ORDER.length minus conditional fields)', () => {
    it('verdicts.length === FIELD_ORDER.length - 1 when availableDate is absent', () => {
      const result = verify({}, {}, provider);
      expect(result.verdicts.length).toBe(FIELD_ORDER.length - 1);
    });

    it('verdicts.length === FIELD_ORDER.length when availableDate is present', () => {
      const result = verify({ availableDate: '07/19/2026' }, {}, provider);
      expect(result.verdicts.length).toBe(FIELD_ORDER.length);
    });
  });

  it('every field is yellow not_provided when both sides are entirely empty', () => {
    const result = verify({}, {}, provider);
    expect(result.verdicts.every((v) => v.status === 'yellow')).toBe(true);
    expect(result.summary).toEqual({ green: 0, yellow: 13, red: 0, total: 13 });
  });

  it('produces a summary that adds up to the verdict count', () => {
    const result = verify(
      { patientName: 'John Smith', quantity: 30 },
      { patientName: 'John Smith', quantity: 30 },
      provider
    );
    const { green, yellow, red, total } = result.summary;
    expect(green + yellow + red).toBe(total);
    expect(total).toBe(13);
  });

  describe('skipDrugLookup (responsiveness: overlay renders every other field immediately, drug row updates in place)', () => {
    it('never calls the provider and marks the drug field pending, while every other field gets its real verdict', () => {
      let providerCalled = false;
      const spyProvider = {
        getConcept: () => {
          providerCalled = true;
          return null;
        }
      };

      const result = verify(
        { patientName: 'John Smith', drug: { name: 'Lisinopril 10mg tablet', ndc: '00071015523' } },
        { patientName: 'John Smith', drug: { name: 'Lisinopril 10mg tablet', ndc: null } as any },
        spyProvider,
        { skipDrugLookup: true }
      );

      expect(providerCalled).toBe(false);

      const nameVerdict = result.verdicts.find((v) => v.field === 'patientName')!;
      expect(nameVerdict.status).toBe('green');

      const drugVerdict = result.verdicts.find((v) => v.field === 'drug')!;
      expect(drugVerdict.status).toBe('yellow');
      expect(drugVerdict.reasonCode).toBe(PENDING_DRUG_LOOKUP_REASON_CODE);
      // The drug NAME is still shown immediately — only the comparison verdict is deferred.
      expect(drugVerdict.sourceValue).toBe('Lisinopril 10mg tablet');
      expect(drugVerdict.enteredValue).toBe('Lisinopril 10mg tablet');
    });

    it('omitting the option (or passing false) behaves exactly like before — a real drug verdict', () => {
      const result = verify(
        { drug: { name: 'Lisinopril 10mg tablet', ndc: '00071015523' } },
        { drug: { name: 'Lisinopril 10mg tablet', ndc: null } as any },
        provider
      );
      const drugVerdict = result.verdicts.find((v) => v.field === 'drug')!;
      expect(drugVerdict.reasonCode).not.toBe(PENDING_DRUG_LOOKUP_REASON_CODE);
      expect(drugVerdict.status).toBe('green');
    });
  });

  describe('refillsFromTotalFills flows from a deserialized PrescriptionRecord through the public verify() entry point (proves the C# overlay contract actually drives compareRefills, not just compareRefills in isolation)', () => {
    it('source refills "4"/refillsFromTotalFills:true vs entered "3" is GREEN (4 total fills = 1 fill + 3 refills)', () => {
      const result = verify({ refills: '4', refillsFromTotalFills: true }, { refills: '3' }, provider);
      const refillsVerdict = result.verdicts.find((v) => v.field === 'refills')!;
      expect(refillsVerdict.status).toBe('green');
      expect(refillsVerdict.reasonCode).toBe('exact_match');
    });

    it('source refills "4"/refillsFromTotalFills:true vs entered "4" is RED (entering the raw total-fills count, not total-1, is a real mismatch)', () => {
      const result = verify({ refills: '4', refillsFromTotalFills: true }, { refills: '4' }, provider);
      const refillsVerdict = result.verdicts.find((v) => v.field === 'refills')!;
      expect(refillsVerdict.status).toBe('red');
      expect(refillsVerdict.reasonCode).toBe('refills_mismatch');
    });

    // Reviewer-requested pin (round 2): EngineModels.cs's JsonSerializerOptions
    // has no DefaultIgnoreCondition set, so the C# overlay literally sends
    // "refillsFromTotalFills":null (not the field omitted) for every
    // ordinary NewRx e-script — RefillsFromTotalFills is only ever set to
    // `true`, never `false` (see EscriptTreeParser.Parse). JS default
    // parameters only kick in for `undefined`, not `null`, so this pins
    // that an explicit JSON `null` still behaves as falsy through the
    // actual JSON.parse -> verify() path, same as omitting the field
    // entirely, rather than relying on that being "obviously fine".
    it('an explicit JSON "refillsFromTotalFills":null (the literal wire shape the C# overlay sends for every non-renewal) behaves as falsy, same as omitting the field', () => {
      const source = JSON.parse('{"refills":"3","refillsFromTotalFills":null}');
      const result = verify(source, { refills: '3' }, provider);
      const refillsVerdict = result.verdicts.find((v) => v.field === 'refills')!;
      expect(refillsVerdict.status).toBe('green');
      expect(refillsVerdict.reasonCode).toBe('exact_match');
      // Pins the exact explanation text, which branches on sourceIsTotalFills
      // (see src/quantity/index.ts compareRefills) — the plain, non-total-fills
      // wording proves null took the falsy branch, not just that both sides
      // happened to compare equal.
      expect(refillsVerdict.explanation).toBe('Refill count matches exactly.');
    });
  });

  it('every verdict includes a reason code and explanation', () => {
    const result = verify({ patientName: 'John Smith' }, { patientName: 'John Doe' }, provider);
    for (const v of result.verdicts) {
      expect(typeof v.reasonCode).toBe('string');
      expect(v.reasonCode.length).toBeGreaterThan(0);
      expect(typeof v.explanation).toBe('string');
      expect(v.explanation.length).toBeGreaterThan(0);
    }
  });

  describe('display values are always clean text, never raw JSON (bug 1 regression)', () => {
    it('renders patientAddress/prescriberAddress as one human-readable line on both sides, never JSON', () => {
      const result = verify(
        {
          patientAddress: { street: '123 Main St', city: 'Testville', state: 'KS', zip: '54321' },
          prescriber: { address: { street: '1 Clinic Way Ste A', city: 'Sampletown', state: 'KS', zip: '12345' } }
        },
        {
          // Entered/overlay shape: freeform street only, every other
          // Address key present but explicitly null (as the C# side
          // serializes it — see overlay/RxVerifyOverlay/Models/EngineModels.cs).
          patientAddress: { street: '123 Main St Testville, KS 54321', unit: null, city: null, state: null, zip: null } as any,
          prescriber: {
            address: { street: '1 Clinic Way Ste A Sampletown, KS 12345', unit: null, city: null, state: null, zip: null } as any
          }
        },
        provider
      );
      const patientAddress = result.verdicts.find((v) => v.field === 'patientAddress')!;
      const prescriberAddress = result.verdicts.find((v) => v.field === 'prescriberAddress')!;

      for (const value of [
        patientAddress.sourceValue,
        patientAddress.enteredValue,
        prescriberAddress.sourceValue,
        prescriberAddress.enteredValue
      ]) {
        expect(value).not.toBeNull();
        expect(value).not.toMatch(/^\{/); // never raw JSON
        expect(typeof value).toBe('string');
      }
      expect(patientAddress.sourceValue).toBe('123 Main St, Testville, KS 54321');
      expect(patientAddress.enteredValue).toBe('123 Main St Testville, KS 54321');
    });

    it('renders drug as name only (never NDC, never JSON) even when ndc is explicitly null', () => {
      const result = verify(
        { drug: { name: 'Clindamycin Phosp 1% Lotion', ndc: '12345-6789-01' } },
        // Entered/overlay shape: Ndc always explicitly null (PioneerRx's
        // entered panel never exposes NDC — see FieldReader.cs ReadEntered).
        { drug: { name: 'Clindamycin Phosp 1% Lotion', ndc: null } as any },
        provider
      );
      const drug = result.verdicts.find((v) => v.field === 'drug')!;
      expect(drug.sourceValue).toBe('Clindamycin Phosp 1% Lotion');
      expect(drug.enteredValue).toBe('Clindamycin Phosp 1% Lotion');
      expect(drug.sourceValue).not.toMatch(/ndc/i);
      expect(drug.enteredValue).not.toMatch(/^\{/);
    });

    // The overlay never touches verify()'s in-memory return value — it
    // only ever sees whatever comes back through JSON.stringify(result)
    // on stdout (see src/cli.ts) and is then JSON-deserialized on the C#
    // side (see overlay/RxVerifyOverlay/Engine/EngineClient.cs,
    // Models/EngineModels.cs FieldVerdict.SourceValue/EnteredValue,
    // both typed `string?`). Asserting only against the in-memory object
    // wouldn't catch a bug that only appears after that JSON hop, so
    // this test goes through JSON.stringify/JSON.parse exactly like the
    // real subprocess boundary does.
    it('address and drug survive the JSON.stringify/parse subprocess boundary as plain strings, never objects', () => {
      const result = verify(
        {
          patientAddress: { street: '123 Main St', city: 'Testville', state: 'KS', zip: '54321' },
          drug: { name: 'Clindamycin Phosp 1% Lotion', ndc: '12345-6789-01' }
        },
        {
          patientAddress: { street: '123 Main St Testville, KS 54321' } as any,
          drug: { name: 'Clindamycin Phosp 1% Lotion', ndc: null } as any
        },
        provider
      );

      const roundTripped = JSON.parse(JSON.stringify(result)) as typeof result;
      const patientAddress = roundTripped.verdicts.find((v) => v.field === 'patientAddress')!;
      const drug = roundTripped.verdicts.find((v) => v.field === 'drug')!;

      for (const value of [patientAddress.sourceValue, patientAddress.enteredValue, drug.sourceValue, drug.enteredValue]) {
        expect(typeof value).toBe('string');
        expect(value).not.toBeInstanceOf(Object);
      }
    });
  });
});
