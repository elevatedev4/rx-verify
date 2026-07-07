import { describe, it, expect } from 'vitest';
import { compareDaw } from '../src/daw/index.js';

describe('compareDaw', () => {
  it('is YELLOW not_provided when the source substitution indicator is missing', () => {
    const r = compareDaw(undefined, true);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is GREEN substitution_allowed when the source allows substitution, regardless of DAW state', () => {
    expect(compareDaw(false, true).status).toBe('green');
    expect(compareDaw(false, false).status).toBe('green');
    expect(compareDaw(false, undefined).status).toBe('green');
  });

  it('is YELLOW not_provided when substitution is not allowed but the entered DAW state was not read', () => {
    const r = compareDaw(true, undefined);
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is GREEN daw_consistent when substitution is not allowed and DAW is checked', () => {
    const r = compareDaw(true, true);
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('daw_consistent');
  });

  it('is RED daw_required when substitution is not allowed and DAW is NOT checked', () => {
    const r = compareDaw(true, false);
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('daw_required');
  });
});
