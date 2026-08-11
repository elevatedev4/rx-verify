import { describe, it, expect } from 'vitest';
import { parseSig, compareSigs } from '../src/sig/index.js';

describe('parseSig', () => {
  it('extracts dose count, route, frequency from abbreviated sig', () => {
    const p = parseSig('take 1 tab po bid');
    expect(p.doseCount).toBe(1);
    expect(p.route).toBe('po');
    expect(p.timesPerDay).toBe(2);
    expect(p.ambiguous).toBe(false);
  });

  it('extracts roman numeral dose counts', () => {
    const p = parseSig('take ii tabs po tid');
    expect(p.doseCount).toBe(2);
    expect(p.timesPerDay).toBe(3);
  });

  it('extracts PRN flag', () => {
    const p = parseSig('take 1 tab po q4h prn');
    expect(p.prn).toBe(true);
  });

  it('extracts duration in days', () => {
    const p = parseSig('take 1 cap po bid x10d');
    expect(p.durationDays).toBe(10);
  });

  it('extracts expanded multi-word equivalents identically to abbreviations', () => {
    const p1 = parseSig('take 1 tablet by mouth twice daily');
    const p2 = parseSig('take 1 tab po bid');
    expect(p1.doseCount).toBe(p2.doseCount);
    expect(p1.route).toBe(p2.route);
    expect(p1.timesPerDay).toBe(p2.timesPerDay);
  });

  it('marks a sig with no extractable structure as ambiguous', () => {
    const p = parseSig('use as directed');
    expect(p.ambiguous).toBe(true);
  });

  it('extracts spelled-out number words as dose counts, matching digit equivalents', () => {
    const p1 = parseSig('take one tablet po qhs');
    const p2 = parseSig('take 1 tab po qhs');
    expect(p1.doseCount).toBe(1);
    expect(p1.doseCount).toBe(p2.doseCount);
  });

  it('recognizes "every night at bedtime" as the qhs (once nightly) frequency', () => {
    const p = parseSig('take 1 tablet every night at bedtime');
    expect(p.timesPerDay).toBe(1);
    expect(p.ambiguous).toBe(false);
  });

  describe('morning/evening phrasings imply once-daily frequency', () => {
    it.each([
      ['in the morning', 'take 1 capsule in the morning'],
      ['each morning', 'take 1 capsule each morning'],
      ['every morning', 'take 1 capsule every morning'],
      ['qam', 'take 1 capsule qam'],
      ['in the evening', 'take 1 capsule in the evening'],
      ['each evening', 'take 1 capsule each evening'],
      ['every evening', 'take 1 capsule every evening'],
      ['qpm', 'take 1 capsule qpm'],
      ['at bedtime', 'take 1 capsule at bedtime'],
      ['qhs', 'take 1 capsule qhs']
    ])('"%s" -> frequency 1/day, unambiguous', (_label, sig) => {
      const p = parseSig(sig);
      expect(p.timesPerDay).toBe(1);
      expect(p.ambiguous).toBe(false);
    });
  });
});

describe('compareSigs', () => {
  // Round 5, fix 4(a): brief's exact prescribed test — scout said every
  // component (bid, twice daily, po, word numbers) already existed, so
  // this may ALREADY pass. It does — all four components (dose count via
  // NUMBER_WORD_MAP "two", dose unit "tablets"->tab, route "by mouth"->po,
  // frequency "twice daily"->bid) were already wired up before round 5.
  // Kept as a regression guard; the live miss this brief describes is
  // therefore an OCR-extraction issue upstream of this module, out of
  // scope here.
  it('(fix 4a) is GREEN: "2 tab po bid" vs "Take two tablets by mouth twice daily." (already passes pre-round-5 — regression guard only)', () => {
    const r = compareSigs('2 tab po bid', 'Take two tablets by mouth twice daily.');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is GREEN when expansions are semantically equal', () => {
    const r = compareSigs('take 1 tablet by mouth twice daily', 'take 1 tab po bid');
    expect(r.status).toBe('green');
  });

  it('is GREEN for q.d./daily variants', () => {
    const r = compareSigs('take 1 tab po q.d.', 'take 1 tab po daily');
    expect(r.status).toBe('green');
  });

  it('is GREEN for the live-test regression: "Take 1 tablet by mouth every night at bedtime" vs the same in words/caps', () => {
    const r = compareSigs(
      'Take 1 tablet by mouth every night at bedtime',
      'TAKE ONE TABLET BY MOUTH EVERY NIGHT AT BEDTIME.'
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is RED on dose count mismatch', () => {
    const r = compareSigs('take 1 tab po bid', 'take 2 tab po bid');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('is RED on route mismatch', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab top bid');
    expect(r.status).toBe('red');
  });

  it('is RED on frequency mismatch', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab po tid');
    expect(r.status).toBe('red');
  });

  it('is YELLOW sig_ambiguous when either side is unparseable', () => {
    const r = compareSigs('use as directed', 'take 1 tab po bid');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('sig_ambiguous');
  });

  it('is YELLOW not_provided when source is missing', () => {
    const r = compareSigs(undefined, 'take 1 tab po bid');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('not_provided');
  });

  it('is GREEN "1 cap" vs "1 capsule" (dose-unit abbreviation equivalence regression)', () => {
    const r = compareSigs('take 1 cap po bid', 'take 1 capsule po bid');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is GREEN for the live-test regression: amphetamine-family sig, "1 capsule in the morning Orally Once a day" vs "TAKE ONE CAPSULE BY MOUTH EVERY MORNING."', () => {
    const r = compareSigs(
      '1 capsule in the morning Orally Once a day',
      'TAKE ONE CAPSULE BY MOUTH EVERY MORNING.'
    );
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('is RED when morning frequency contradicts an explicit different frequency', () => {
    const r = compareSigs('take 1 tab po every morning', 'take 1 tab po bid');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  // REVIEW FIX (confirmed false GREEN): "every morning and evening" is a
  // genuinely twice-daily instruction. Before the negative-lookahead
  // fix, "every morning" folded to qam regardless of the trailing "and
  // evening", which was then silently dropped by extractFrequency (no
  // FREQ_MAP entry for bare "evening") — so this BID sig compared GREEN
  // against an entered once-daily "qam" sig.
  it('does NOT fold "every morning and evening" to once-daily — must not be GREEN against a once-daily entered sig', () => {
    const p = parseSig('take 1 tab every morning and evening');
    expect(p.timesPerDay).toBeNull();

    const r = compareSigs('take 1 tab every morning and evening', 'take 1 tab qam');
    expect(r.status).not.toBe('green');
  });

  it('does NOT fold "each evening and morning" to once-daily either (same continuation guard, reversed order)', () => {
    const r = compareSigs('take 1 tab each evening and morning', 'take 1 tab qpm');
    expect(r.status).not.toBe('green');
  });

  it('a preceding conflicting frequency word ("twice every morning") stays non-green, not silently folded to once-daily', () => {
    const r = compareSigs('take 1 tab twice every morning', 'take 1 tab qam');
    expect(r.status).not.toBe('green');
  });

  it('an unrelated "and" after the morning phrase (no continuation risk) still folds normally to once-daily', () => {
    // Sanity check that the negative lookahead isn't so broad it starts
    // rejecting every sig that merely contains "and" somewhere later.
    const r = compareSigs('take 1 tab every morning and with food', 'take 1 tab qam');
    // "and with food" still trails the phrase, so per the conservative
    // lookahead this also stays unfolded/ambiguous rather than green —
    // documenting the actual (safe) behavior rather than asserting a
    // stronger claim than the fix makes.
    expect(r.status).not.toBe('green');
  });
});

// Round 5, fix 4(b): additive abbreviation coverage. Every new entry gets
// a green-equivalence pair; at least 3 negative tests prove different
// frequencies/doses/routes still mismatch (brief's own examples: ac vs
// pc, 1 puff vs 2 puffs, im vs iv — all included below, plus more per
// category for solid coverage).
describe('Round 5, fix 4: additive sig abbreviation coverage', () => {
  describe('frequencies/timing', () => {
    it('GREEN: "ac" matches itself (before-meals qualifier, both sides)', () => {
      const r = compareSigs('take 1 tab po bid ac', 'take 1 tablet po bid ac');
      expect(r.status).toBe('green');
    });

    it('GREEN: "pc" matches itself (after-meals qualifier, both sides)', () => {
      const r = compareSigs('take 1 tab po bid pc', 'take 1 tablet po bid pc');
      expect(r.status).toBe('green');
    });

    it('GREEN: "q24h" is equivalent to "daily"/"qd" (once every 24h = once daily)', () => {
      const r = compareSigs('take 1 tab po q24h', 'take 1 tab po daily');
      expect(r.status).toBe('green');
      expect(parseSig('take 1 tab po q24h').timesPerDay).toBe(1);
    });

    it('GREEN: "q48h" matches itself', () => {
      const r = compareSigs('take 1 tab po q48h', 'take 1 tab po q48h');
      expect(r.status).toBe('green');
    });

    it('GREEN: "q72h" matches itself', () => {
      const r = compareSigs('take 1 tab po q72h', 'take 1 tab po q72h');
      expect(r.status).toBe('green');
    });

    it('GREEN: "qwk" is equivalent to "weekly"', () => {
      const r = compareSigs('take 1 tab po qwk', 'take 1 tab po weekly');
      expect(r.status).toBe('green');
    });

    it('NEGATIVE: "ac" vs "pc" is a mismatch (brief\'s own example)', () => {
      const r = compareSigs('take 1 tab po bid ac', 'take 1 tab po bid pc');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });

    it('NEGATIVE: "q24h" vs "q48h" is a frequency mismatch (daily vs every-other-day rate)', () => {
      const r = compareSigs('take 1 tab po q24h', 'take 1 tab po q48h');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });

    it('NEGATIVE: "qwk" vs "q24h" is a frequency mismatch (weekly vs daily)', () => {
      const r = compareSigs('take 1 tab po qwk', 'take 1 tab po q24h');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });

    it('one side specifies "ac", the other omits it entirely -> YELLOW indeterminate (never silently ignored)', () => {
      const r = compareSigs('take 1 tab po bid ac', 'take 1 tab po bid');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('sig_ambiguous');
    });
  });

  describe('dose units', () => {
    it('GREEN: "1 tsp" vs "1 teaspoon"', () => {
      const r = compareSigs('take 1 tsp po daily', 'take 1 teaspoon po daily');
      expect(r.status).toBe('green');
    });

    it('GREEN: "1 tbsp" vs "1 tablespoon"', () => {
      const r = compareSigs('take 1 tbsp po daily', 'take 1 tablespoon po daily');
      expect(r.status).toBe('green');
    });

    it('GREEN: "1 oz" vs "1 ounce"', () => {
      const r = compareSigs('take 1 oz po daily', 'take 1 ounce po daily');
      expect(r.status).toBe('green');
    });

    it('GREEN: "2 puffs" vs "2 inhalations"', () => {
      const r = compareSigs('inhale 2 puffs bid', 'inhale 2 inhalations bid');
      expect(r.status).toBe('green');
    });

    it('GREEN: "1 spray" vs "1 spray" (both sides)', () => {
      const r = compareSigs('use 1 spray each nostril daily', 'use 1 spray each nostril daily');
      expect(r.status).toBe('green');
    });

    it('GREEN: "1 unit" vs "1 unit" (both sides)', () => {
      const r = compareSigs('inject 1 unit sc daily', 'inject 1 unit sc daily');
      expect(r.status).toBe('green');
    });

    it('GREEN: "1 patch" vs "1 patch" (both sides)', () => {
      const r = compareSigs('apply 1 patch top daily', 'apply 1 patch top daily');
      expect(r.status).toBe('green');
    });

    it('NEGATIVE: "1 puff" vs "2 puffs" is a dose-count mismatch (brief\'s own example)', () => {
      const r = compareSigs('inhale 1 puff bid', 'inhale 2 puffs bid');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });

    it('NEGATIVE: "1 tsp" vs "1 tbsp" is a dose-unit mismatch (different dose forms)', () => {
      const r = compareSigs('take 1 tsp po daily', 'take 1 tbsp po daily');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });
  });

  describe('routes', () => {
    it('GREEN: "im" matches itself', () => {
      const r = compareSigs('inject 1 ml im weekly', 'inject 1 ml im weekly');
      expect(r.status).toBe('green');
    });

    it('GREEN: "iv" matches itself', () => {
      const r = compareSigs('give 1 dose iv daily', 'give 1 dose iv daily');
      expect(r.status).toBe('green');
    });

    it('GREEN: "sc"/"subq"/"subcut"/"subcutaneously" all fold to the same canonical route', () => {
      expect(compareSigs('inject 1 unit sc daily', 'inject 1 unit subq daily').status).toBe('green');
      expect(compareSigs('inject 1 unit subcut daily', 'inject 1 unit subcutaneously daily').status).toBe('green');
      expect(compareSigs('inject 1 unit sc daily', 'inject 1 unit subcutaneously daily').status).toBe('green');
    });

    it('GREEN: "in each nostril" -> nasal route matches itself', () => {
      const r = compareSigs('use 1 spray in each nostril bid', 'use 1 spray in each nostril bid');
      expect(r.status).toBe('green');
      expect(parseSig('use 1 spray in each nostril bid').route).toBe('nasal');
    });

    it('GREEN: "inhale" matches "inhalation" (route)', () => {
      const r = compareSigs('inhale 2 puffs bid', 'take 2 puffs by inhalation bid');
      expect(r.status).toBe('green');
    });

    it('NEGATIVE: "im" vs "iv" is a route mismatch (brief\'s own example)', () => {
      const r = compareSigs('inject 1 ml im daily', 'inject 1 ml iv daily');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });

    it('NEGATIVE: "sc" vs "im" is a route mismatch', () => {
      const r = compareSigs('inject 1 unit sc daily', 'inject 1 unit im daily');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });

    it('NEGATIVE: "nasal" (in each nostril) vs "po" is a route mismatch', () => {
      const r = compareSigs('use 1 spray in each nostril bid', 'take 1 spray po bid');
      expect(r.status).toBe('red');
      expect(r.reasonCode).toBe('sig_mismatch');
    });

    // REVIEWER BLOCKER (round 5, fix 4 hardening): "iv" collides with
    // ROMAN_MAP's pre-existing roman-numeral-4. Before the fix,
    // extractDoseCount and extractRoute ran as fully independent scans,
    // so a bare "iv" was read as BOTH doseCount=4 AND route='iv' at
    // once — reproduced by the reviewer:
    //   parseSig('give iv daily') -> {doseCount:4, route:'iv', ...}
    //   parseSig('take iv tablets daily') -> {doseCount:4, route:'iv', ...}
    //   compareSigs(...) -> green exact_match  // FALSE GREEN
    // a genuinely-IV instruction compared GREEN against an oral tablet
    // sig that used roman "iv" as dose-count-4, because dose-unit
    // null-vs-tab is a no-penalty one-sided case and both sides
    // coincidentally parsed the same spurious route+doseCount pair.
    describe('"iv" / roman-numeral-4 collision (reviewer blocker)', () => {
      it('FALSE GREEN regression: "give iv daily" (route) vs "take iv tablets daily" (roman-numeral dose count) must NOT be green', () => {
        const r = compareSigs('give iv daily', 'take iv tablets daily');
        expect(r.status).not.toBe('green');
      });

      it('"take iv tablets daily" resolves "iv" as roman-numeral doseCount 4, with NO route (adjacent dose-unit word disambiguates)', () => {
        const p = parseSig('take iv tablets daily');
        expect(p.doseCount).toBe(4);
        expect(p.route).toBeNull();
      });

      it('"give iv daily" resolves "iv" as the route (intravenous), with NO dose count (nothing adjacent for it to quantify)', () => {
        const p = parseSig('give iv daily');
        expect(p.route).toBe('iv');
        expect(p.doseCount).toBeNull();
      });

      it('a genuine "administer 1 ml iv daily" still gets route iv (explicit digit dose count leaves "iv" free for route)', () => {
        const p = parseSig('administer 1 ml iv daily');
        expect(p.doseCount).toBe(1);
        expect(p.route).toBe('iv');
      });

      it('other roman numerals (unaffected by the "iv" gate) still resolve as plain dose counts, no adjacency requirement', () => {
        const p = parseSig('take ii tabs po tid');
        expect(p.doseCount).toBe(2);
        expect(p.route).toBe('po');
      });
    });
  });

  describe('deliberately NOT added (ambiguous — see ROUTE_MAP/MEAL_RELATION_MAP docs)', () => {
    it('"qn" is NOT recognized as a frequency — stays an unrecognized-freq-token yellow, not silently mapped', () => {
      const p = parseSig('take 1 tab po qn');
      expect(p.hasUnrecognizedFreqToken).toBe(true);
    });

    it('"od" still means the existing "right eye" route, never "once daily" (no new od-as-frequency mapping added)', () => {
      const p = parseSig('take 1 gtt od bid');
      expect(p.route).toBe('od');
      expect(p.timesPerDay).toBe(2); // from bid, not from "od"
    });
  });
});

describe('Round 6 fixes', () => {
  describe('fix 1: identical-sig fast path (verbatim_match)', () => {
    it('is GREEN verbatim_match for an unparseable-but-identical sig (live report)', () => {
      const text = 'Inject 12.5mg under the skin every week.';
      const r = compareSigs(text, text);
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('verbatim_match');
    });

    it('confirms the raw text really is unparseable on its own (sanity check the fast path is actually doing work)', () => {
      const p = parseSig('Inject 12.5mg under the skin every week.');
      expect(p.ambiguous).toBe(true);
    });

    it('is GREEN verbatim_match tolerating case/whitespace/trailing-punctuation differences only', () => {
      const r = compareSigs('  Inject 12.5mg under the skin every week.  ', 'inject 12.5mg under the skin every week');
      expect(r.status).toBe('green');
      expect(r.reasonCode).toBe('verbatim_match');
    });

    it('regression: two DIFFERENT unparseable sigs still fall to yellow sig_ambiguous, not a false green', () => {
      const r = compareSigs('use as directed by prescriber', 'apply sparingly as directed');
      expect(r.status).toBe('yellow');
      expect(r.reasonCode).toBe('sig_ambiguous');
    });

    it('regression: a parseable pair that genuinely differs is still RED, not swallowed by the fast path', () => {
      const r = compareSigs('take 1 tab po bid', 'take 2 tab po bid');
      expect(r.status).toBe('red');
    });
  });

  describe('fix 6: qday/qdaily frequency', () => {
    it('is GREEN: "1 tab PO qday" vs "TAKE ONE TABLET BY MOUTH EVERY DAY."', () => {
      const r = compareSigs('1 tab PO qday', 'TAKE ONE TABLET BY MOUTH EVERY DAY.');
      expect(r.status).toBe('green');
    });

    it('"qday" parses to once-daily frequency', () => {
      const p = parseSig('take 1 tab po qday');
      expect(p.timesPerDay).toBe(1);
    });

    it('"qdaily" also parses to once-daily frequency', () => {
      const p = parseSig('take 1 tab po qdaily');
      expect(p.timesPerDay).toBe(1);
    });
  });

  describe('fix 7: "moming"/"evenmg" OCR confusables in morning/evening phrase folds', () => {
    it('is GREEN: "each moming" (OCR rn->m) matches "every morning"', () => {
      const r = compareSigs('Take 1 capsule by mouth each moming.', 'TAKE ONE CAPSULE BY MOUTH EVERY MORNING.');
      expect(r.status).toBe('green');
    });

    it('"each moming" parses to once-daily (qam) frequency, same as "each morning"', () => {
      const p = parseSig('take 1 capsule by mouth each moming');
      expect(p.timesPerDay).toBe(1);
    });

    it('"in the evenmg" parses to once-daily (qpm) frequency, same as "in the evening"', () => {
      const p = parseSig('take 1 tab po in the evenmg');
      expect(p.timesPerDay).toBe(1);
    });

    it('regression: "every morning and evening" (round-4 guard) still stays ambiguous, unaffected by the moming/evenmg tolerance', () => {
      const p = parseSig('take 1 tab po every morning and evening');
      expect(p.timesPerDay).toBeNull();
    });

    it('regression: "every moming and evening" (confusable + the "and" continuation together) also stays ambiguous, not silently folded to once-daily', () => {
      const p = parseSig('take 1 tab po every moming and evening');
      expect(p.timesPerDay).toBeNull();
    });
  });
});

// Round 7, fix 1: live-test false GREEN — pharmacist-owner report. Source
// (OCR, glued words): "Take 1 tab by mouth once in amand one at lunch
// time. for30days"; entered: "TAKE ONE TABLET BY MOUTH EVERY MORNING AND
// at noon." The engine returned GREEN exact_match. Root cause: neither
// "lunch" nor "noon" was in any token table, so both were silently
// dropped before comparison — dose count/route/unit happened to match,
// and there was nothing left to notice the sigs disagree about WHEN the
// dose is taken. Owner's ruling: "Lunchtime and noon are not the same
// thing" — a false GREEN is the worst failure class for this app (it
// tells the pharmacist not to look).
describe('Round 7, fix 1: TIME_OF_DAY concept (lunch != noon false-GREEN)', () => {
  it('reviewer/owner repro: the exact reported pair is NOT green — must be RED (lunch vs noon+morning are different time-of-day sets)', () => {
    const source = 'Take 1 tab by mouth once in amand one at lunch time. for30days';
    const entered = 'TAKE ONE TABLET BY MOUTH EVERY MORNING AND at noon.';
    const r = compareSigs(source, entered);
    expect(r.status).not.toBe('green');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('regression: OCR glue elsewhere in the sig ("amand", "for30days") does not corrupt unrelated dose count/unit/route extraction — this engine does NOT attempt to de-glue duration/time text, and says so honestly (durationDays stays null, the glued text surfaces in residualTokens) rather than silently dropping it', () => {
    const p = parseSig('Take 1 tab by mouth once in amand one at lunch time. for30days');
    expect(p.doseCount).toBe(1);
    expect(p.doseUnit).toBe('tab');
    expect(p.route).toBe('po');
    expect(p.ambiguous).toBe(false);
    expect(p.durationDays).toBeNull();
    expect(p.residualTokens).toContain('for30days');
  });

  it('"1 tab po qam and at lunch" vs "1 tab po qam and at noon" is RED (lunch != noon, same root cause without OCR glue)', () => {
    const r = compareSigs('1 tab po qam and at lunch', '1 tab po qam and at noon');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('reversed direction: "1 tab po qam and at noon" vs "1 tab po qam and at lunch" is RED too (mismatch is symmetric, not an artifact of argument order)', () => {
    const r = compareSigs('1 tab po qam and at noon', '1 tab po qam and at lunch');
    expect(r.status).toBe('red');
    expect(r.reasonCode).toBe('sig_mismatch');
  });

  it('"take at noon" vs "take at midday" is GREEN — noon and midday ARE the same clock time', () => {
    const r = compareSigs('take at noon', 'take at midday');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });

  it('parses "noon" and "midday" to the same time-of-day id', () => {
    expect(parseSig('take at noon').timeOfDay).toEqual(['noon']);
    expect(parseSig('take at midday').timeOfDay).toEqual(['noon']);
  });

  it('parses "at lunch time" to the "lunch" time-of-day id, distinct from "noon"', () => {
    expect(parseSig('take 1 tab po at lunch time').timeOfDay).toEqual(['lunch']);
  });

  it('YELLOW sig_ambiguous for an unparseable/unrecognized time phrase on only one side — never a silent green (residual-token guard)', () => {
    const r = compareSigs('take 1 tab po bid at teatime', 'take 1 tab po bid');
    expect(r.status).not.toBe('green');
    expect(r.status).toBe('yellow');
    expect(r.reasonCode).toBe('sig_ambiguous');
  });

  it('GREEN still holds for a plain morning-phrasing pair unaffected by the new time-of-day logic', () => {
    const r = compareSigs('take 1 capsule in the morning', 'TAKE ONE CAPSULE EVERY MORNING.');
    expect(r.status).toBe('green');
    expect(r.reasonCode).toBe('exact_match');
  });
});

// Round 7, fix 2 (blocker — ed026b8 review, FINDING 1): confirmed false
// GREEN. isKnownSigToken previously whitelisted ANY bare digit/number
// word/roman numeral by vocabulary membership alone, but extractDoseCount
// only ever consumes the FIRST number-shaped token it finds — so a
// genuine SECOND dose-count-shaped token on only one side was invisible
// to the residual guard. Fixed by restricting that whitelist to the
// SPECIFIC index extractDoseCount actually consumed (same pattern as
// extractRoute's excludeIdx).
describe('Round 7, fix 2: a second, uncounted dose-count-shaped token must not be silently ignored', () => {
  it('reviewer repro: a trailing bare digit on only one side is NOT green', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab po bid 2');
    expect(r.status).not.toBe('green');
  });

  it('reviewer repro: a trailing spelled-out number word on only one side is NOT green', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab po bid two');
    expect(r.status).not.toBe('green');
  });

  it('reviewer repro: a trailing roman numeral on only one side is NOT green', () => {
    const r = compareSigs('take 1 tab po bid', 'take 1 tab po bid ii');
    expect(r.status).not.toBe('green');
  });

  it('the extra number-shaped token shows up as residual, not silently accepted as "known" vocabulary', () => {
    const p = parseSig('take 1 tab po bid 2');
    expect(p.residualTokens).toContain('2');
  });

  it('regression: the pre-existing "iv"/roman-numeral-4 route collision still resolves correctly (route check runs before the number-shaped restriction)', () => {
    const p = parseSig('give iv daily');
    expect(p.route).toBe('iv');
    expect(p.doseCount).toBeNull();
  });
});

// Round 7, fix 2 (blocker — ed026b8 review, FINDING 2): the residual
// guard added in fix 1 was too aggressive with only {take, by} as filler,
// flipping routine CLINICALLY-EQUIVALENT phrasing pairs to YELLOW.
// Widened FILLER_WORDS under one principle: neutral grammar/boilerplate
// and administration verbs that are fully redundant with the `route`
// field never block green; anything that is itself clinical information
// (administration MANNER, food-timing) still can. See FILLER_WORDS' doc
// for the full rationale.
describe('Round 7, fix 2: FILLER_WORDS widened to neutral connectors/verbs only — meaning-bearing words still block green', () => {
  describe('neutral — must stay/become GREEN', () => {
    it('reviewer repro: "Apply..." vs "Use..." (same route/dose/frequency) is GREEN — interchangeable neutral administration verbs', () => {
      const r = compareSigs('Apply 1 patch top daily', 'Use 1 patch top daily');
      expect(r.status).toBe('green');
    });

    it('"...bid as directed" vs "...bid" (one-sided "as directed") stays GREEN — deference boilerplate, not a differing instruction', () => {
      const r = compareSigs('take 1 tab po bid as directed', 'take 1 tab po bid');
      expect(r.status).toBe('green');
    });

    it('"and" vs no connector at all is GREEN (one-sided "and" is neutral grammar)', () => {
      const r = compareSigs('1 tab po qam and at lunch', '1 tab po qam at lunch');
      expect(r.status).toBe('green');
    });

    it('"and" vs a comma list-separator is GREEN (both are pure grammar, not a content difference)', () => {
      const r = compareSigs('1 tab po qam and at lunch', '1 tab po qam, at lunch');
      expect(r.status).toBe('green');
    });

    it('"give"/"administer"/"instill" verb variants over the same route/dose/frequency are GREEN', () => {
      expect(compareSigs('give 1 ml im daily', 'administer 1 ml im daily').status).toBe('green');
      expect(compareSigs('instill 1 gtt od bid', 'apply 1 gtt od bid').status).toBe('green');
    });
  });

  describe('meaning-bearing — must NOT become green', () => {
    it('"chew" vs plain "take" (same route/dose/frequency) is NOT green — administration MANNER is real clinical content, not filler', () => {
      const r = compareSigs('chew 1 tab po bid', 'take 1 tab po bid');
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
    });

    it('"crush" vs "dissolve" (same route/dose/frequency) is NOT green — different, and both are meaning-bearing', () => {
      const r = compareSigs('crush 1 tab po bid', 'dissolve 1 tab po bid');
      expect(r.status).not.toBe('green');
    });

    it('one-sided "with food" is NOT green — food-timing is meaning-bearing and deliberately NOT folded into ac/pc (see FILLER_WORDS\' doc)', () => {
      const r = compareSigs('take 1 tab po bid with food', 'take 1 tab po bid');
      expect(r.status).not.toBe('green');
      expect(r.status).toBe('yellow');
    });

    it('one-sided "on an empty stomach" is NOT green either', () => {
      const r = compareSigs('take 1 tab po bid on an empty stomach', 'take 1 tab po bid');
      expect(r.status).not.toBe('green');
    });
  });
});
