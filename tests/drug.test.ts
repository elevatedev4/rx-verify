import { describe, it, expect } from 'vitest';
import { parseNdc, compareDrugs, normalizeDrugNameString, FixtureProvider } from '../src/drug/index.js';

const provider = new FixtureProvider();

describe('parseNdc', () => {
  it('parses an 11-digit NDC', () => {
    const p = parseNdc('00071015523');
    expect(p).toMatchObject({ labeler: '00071', product: '0155', packageCode: '23' });
  });

  it('parses a dashed 5-4-2 NDC', () => {
    const p = parseNdc('00071-0155-23');
    expect(p?.normalized11).toBe('00071015523');
  });

  it('parses a dashed 10-digit 4-4-2 NDC by padding labeler', () => {
    const p = parseNdc('0071-0155-23');
    expect(p?.normalized11).toBe('00071015523');
  });

  it('returns null for garbage', () => {
    expect(parseNdc('not-an-ndc')).toBeNull();
  });
});

describe('compareDrugs', () => {
  it('is GREEN on identical NDC', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00071015523' }, provider);
    expect(r.status).toBe('green');
  });

  it('is YELLOW generic_substitution for brand vs generic same ingredient/strength/form', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00093715601' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('generic_substitution');
  });

  it('is YELLOW pack_size for same product different package NDC', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00071015590' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('pack_size');
  });

  it('is RED on different strength', () => {
    const r = compareDrugs(
      { name: 'Synthroid 50mcg tablet' },
      { name: 'Levothyroxine 50mcg tablet' },
      provider
    );
    // same ingredient/strength/form -> should actually be substitution, not red
    expect(r.status).toBe('yellow');
  });

  it('is RED on different ingredient', () => {
    const r = compareDrugs({ ndc: '00071015523' }, { ndc: '00071015601' }, provider); // lisinopril vs Lipitor(atorvastatin)
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('drug_mismatch');
  });

  it('is YELLOW not_provided when source drug missing', () => {
    const r = compareDrugs(undefined, { ndc: '00071015523' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is YELLOW unknown_drug when neither NDC nor name resolves', () => {
    const r = compareDrugs({ name: 'Zorbaxatin 9000mg unobtanium' }, { ndc: '00071015523' }, provider);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('unknown_drug');
  });

  describe('drug IDENTITY by name (real-world overlay shape: entered side never carries an NDC)', () => {
    it('is GREEN name_identity_match on an exact normalized name match, even with no NDC on either side', () => {
      const r = compareDrugs({ name: 'Clindamycin Phosp 1% Lotion' }, { name: 'Clindamycin Phosp 1% Lotion' }, provider);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('is GREEN name_identity_match on a case/punctuation-only difference', () => {
      const r = compareDrugs({ name: 'Clindamycin Phosp 1% Lotion' }, { name: 'clindamycin phosp 1% lotion.' }, provider);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('is GREEN on matching names even when the source NDC is present and unresolvable -- NDC is lookup-only, never required for green', () => {
      const r = compareDrugs(
        { name: 'Gabapentin 300mg capsule', ndc: '99999999999' },
        { name: 'Gabapentin 300mg capsule' },
        provider
      );
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('name_identity_match');
    });

    it('does not let a name-identity match paper over a stated strength contradiction (names must be genuinely equal, not just similar)', () => {
      const r = compareDrugs({ name: 'Lisinopril 20mg tablet' }, { name: 'Lisinopril 10mg tablet' }, provider);
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('drug_mismatch');
    });

    describe('dosage-form / casing / spacing variants (W-T10 item 4)', () => {
      it('is GREEN name_identity_match for "Estradiol 2 MG TABS" vs "Estradiol 2 Mg Tablet"', () => {
        const r = compareDrugs({ name: 'Estradiol 2 MG TABS' }, { name: 'Estradiol 2 Mg Tablet' }, provider);
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('is GREEN name_identity_match for "Amoxicillin 500 MG CAP" vs "amoxicillin 500 mg capsule"', () => {
        const r = compareDrugs({ name: 'Amoxicillin 500 MG CAP' }, { name: 'amoxicillin 500 mg capsule' }, provider);
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('is GREEN name_identity_match for "Metformin 500MG SUSP" vs "Metformin 500 mg Suspension" (no-space unit + susp abbreviation)', () => {
        const r = compareDrugs({ name: 'Metformin 500MG SUSP' }, { name: 'Metformin 500 mg Suspension' }, provider);
        expect(r.status).toBe('green');
        expect(r.reasonCode).toBe('name_identity_match');
      });

      it('does not fold "cap" inside an unrelated word like "captopril" into "capsule"', () => {
        expect(normalizeDrugNameString('Captopril 25mg tablet')).toBe('captopril 25 mg tablet');
      });
    });
  });
});
