import { describe, it, expect } from 'vitest';
import { verify, PENDING_DRUG_LOOKUP_REASON_CODE } from '../src/engine/index.js';
import { FixtureProvider } from '../src/drug/index.js';
import { FIELD_ORDER } from '../src/types.js';

const provider = new FixtureProvider();

describe('verify engine', () => {
  it('always returns verdicts in FIELD_ORDER', () => {
    const result = verify({}, {}, provider);
    expect(result.verdicts.map((v) => v.field)).toEqual([...FIELD_ORDER]);
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
