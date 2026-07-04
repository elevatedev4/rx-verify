import { describe, it, expect } from 'vitest';
import { compareNames, parseName } from '../src/normalize/name.js';

describe('parseName', () => {
  it('parses "First Last" order', () => {
    expect(parseName('John Smith')).toMatchObject({ first: 'john', last: 'smith' });
  });

  it('parses "Last, First" order', () => {
    expect(parseName('Smith, John')).toMatchObject({ first: 'john', last: 'smith' });
  });

  it('folds case and punctuation', () => {
    expect(parseName("O'Brien, Mary")).toMatchObject({ first: 'mary' });
  });
});

describe('compareNames', () => {
  it('is GREEN on exact normalized match regardless of order', () => {
    const r1 = compareNames('John Smith', 'Smith, John');
    expect(r1.status).toBe('green');
    expect(r1.reasonCode).toBe('exact_match');
  });

  it('is GREEN on case/punctuation differences', () => {
    const r = compareNames('john smith', 'JOHN SMITH');
    expect(r.status).toBe('green');
  });

  it('tolerates hyphenated surnames', () => {
    const r = compareNames('Maria Garcia-Lopez', 'Maria Garcia Lopez');
    expect(r.status).toBe('green');
  });

  it('is YELLOW nickname_match for known nickname pairs', () => {
    const r = compareNames('William Jones', 'Bill Jones');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('nickname_match');
  });

  it('is YELLOW nickname_match for Peggy/Margaret', () => {
    const r = compareNames('Margaret Lee', 'Peggy Lee');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('nickname_match');
  });

  it('never returns GREEN for a nickname match', () => {
    const r = compareNames('Robert Chen', 'Bob Chen');
    expect(r.status).not.toBe('green');
  });

  it('is RED on differing surname', () => {
    const r = compareNames('John Smith', 'John Doe');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('surname_mismatch');
  });

  it('is YELLOW not_provided when source is missing', () => {
    const r = compareNames(undefined, 'John Smith');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is YELLOW not_provided when entered is missing', () => {
    const r = compareNames('John Smith', '');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('never returns a RED mismatch when source is absent', () => {
    const r = compareNames(null, 'Anything Here');
    expect(r.status).not.toBe('red');
  });
});
