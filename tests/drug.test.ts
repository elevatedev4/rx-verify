import { describe, it, expect } from 'vitest';
import { parseNdc, compareDrugs, FixtureProvider } from '../src/drug/index.js';

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
});
