import { describe, it, expect } from 'vitest';
import { verify } from '../src/engine/index.js';
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
    expect(result.summary).toEqual({ green: 0, yellow: 10, red: 0, total: 10 });
  });

  it('produces a summary that adds up to the verdict count', () => {
    const result = verify(
      { patientName: 'John Smith', quantity: 30 },
      { patientName: 'John Smith', quantity: 30 },
      provider
    );
    const { green, yellow, red, total } = result.summary;
    expect(green + yellow + red).toBe(total);
    expect(total).toBe(10);
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
});
