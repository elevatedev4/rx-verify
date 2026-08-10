/**
 * Sig (directions for use) parsing + comparison.
 *
 * Approach: expand well-known abbreviations on BOTH sides into a
 * canonical structured representation, then compare structurally.
 * Expansion itself is never treated as a "difference" — "po bid" and
 * "by mouth twice daily" are the same instruction.
 *
 * Extracted fields where possible: dose count, route, frequency (times
 * per day), prn flag, duration in days.
 *
 *  - Semantic equality after expansion = GREEN
 *  - Mismatch in dose / route / frequency = RED
 *  - Either side unparseable/ambiguous = YELLOW sig_ambiguous
 */

export type SigCompareStatus = 'green' | 'yellow' | 'red';

export interface SigCompareResult {
  status: SigCompareStatus;
  reasonCode: string;
  explanation: string;
}

export interface ParsedSig {
  doseCount: number | null;
  doseUnit: string | null; // tab, cap, ml, gtt, etc (normalized)
  route: string | null; // po, top, sl, pr, od, os, ou
  timesPerDay: number | null;
  prn: boolean;
  durationDays: number | null;
  /**
   * Round 5, fix 4 (additive): meal-timing qualifier — 'ac' (before
   * meals) / 'pc' (after meals). See MEAL_RELATION_MAP's doc.
   */
  mealRelation: string | null;
  /** true if we could not confidently extract enough structure. */
  ambiguous: boolean;
  /**
   * true if the sig contains a token that LOOKS like a frequency
   * abbreviation (q-something) but is not in our table. Safety rule:
   * an unrecognized frequency token means the comparison is
   * indeterminate — never silently skipped.
   */
  hasUnrecognizedFreqToken: boolean;
}

const ROUTE_MAP: Record<string, string> = {
  po: 'po', 'p.o.': 'po', 'by mouth': 'po', orally: 'po', oral: 'po',
  pr: 'pr', rectally: 'pr', rectal: 'pr',
  sl: 'sl', sublingual: 'sl', 'sublingually': 'sl',
  top: 'top', topically: 'top', topical: 'top',
  od: 'od', // right eye
  os: 'os', // left eye
  ou: 'ou', // both eyes
  // Round 5, fix 4 (additive): common, unambiguous injection/nasal/
  // inhalation routes. "iv" ALSO happens to be ROMAN_MAP's roman numeral
  // for 4 (pre-existing) — reviewer confirmed this produced a real false
  // GREEN (extractDoseCount and extractRoute ran as independent scans,
  // so a bare "iv" was read as BOTH doseCount=4 AND route='iv' at once,
  // making an IV-route sig with no dose unit compare identically to an
  // oral "iv tablets" = 4-tablets sig). Fixed at the extractor level —
  // see extractDoseCount's doc — not by removing this entry; "iv" stays
  // a fully valid route here.
  im: 'im', // intramuscular
  iv: 'iv', // intravenous
  sc: 'sc', subq: 'sc', subcut: 'sc', subcutaneously: 'sc', // all fold to one canonical
  // "in each nostril" is substituted (MULTI_WORD_TERMS) directly to the
  // 'nasal' token below before tokenization.
  nasal: 'nasal',
  inhale: 'inh', inhalation: 'inh'
};

/**
 * Round 5, fix 4 (additive): meal-timing qualifier — "ac" (before meals)
 * / "pc" (after meals). Deliberately its own small field/map rather than
 * folded into ROUTE_MAP or FREQ_MAP: it's neither a route nor a times-
 * per-day count, it's an independent qualifier a sig can carry alongside
 * either (e.g. "1 tab po bid ac"). Compared the same conservative way as
 * route/frequency in compareSigs — both present and different is a
 * contradiction (RED); only one side specifying it is indeterminate
 * (YELLOW), never silently ignored.
 *
 * Deliberately NOT added: "qn" and "od" as a once-daily variant — both
 * flagged ambiguous in the branch brief ("qn" collides with "qhs"/"qod"-
 * style once-nightly/every-other-day shorthand depending on source; "od"
 * is already this table's "right eye" route, and using it for "once
 * daily" too would make a bare "od" token silently mean two different
 * things depending on context).
 */
const MEAL_RELATION_MAP: Record<string, string> = {
  ac: 'ac',
  pc: 'pc'
};

/** Frequency abbreviations -> times per day. */
const FREQ_MAP: Record<string, number> = {
  qd: 1, 'q.d.': 1, daily: 1, qam: 1, qpm: 1, qhs: 1, hs: 1,
  qod: 0.5, 'q.o.d.': 0.5, // every other day
  bid: 2, 'b.i.d.': 2, 'twice daily': 2, 'twice a day': 2, twice: 2,
  tid: 3, 't.i.d.': 3, 'three times daily': 3, 'three times a day': 3,
  qid: 4, 'q.i.d.': 4, 'four times daily': 4, 'four times a day': 4,
  q4h: 6, q6h: 4, q8h: 3, q12h: 2,
  // Round 5, fix 4 (additive): longer-interval "every N hours"/weekly
  // dosing, expressed as an equivalent times-per-day rate so they compare
  // structurally against each other and against any other frequency the
  // same way q4h/q6h/etc. already do.
  q24h: 1, // once every 24h = once daily
  q48h: 0.5, // once every 48h
  q72h: 1 / 3, // once every 72h
  qwk: 1 / 7, weekly: 1 / 7,
  // Round 6, fix 6 (additive): "qday"/"qdaily" is a common informal
  // once-daily shorthand (not the standard "qd" abbreviation, but seen
  // verbatim in live e-scripts) — same rate as qd/daily above. "q.day"
  // included too (trivial punctuation variant); unlike qd/bid/etc, the
  // internal "." here isn't stripped by preprocess()'s punctuation
  // cleanup, so it's listed as its own literal key rather than relying
  // on that pass.
  qday: 1, qdaily: 1, 'q.day': 1
};

const PRN_TOKENS = new Set(['prn', 'p.r.n.', 'as needed']);

const DOSE_UNIT_MAP: Record<string, string> = {
  tab: 'tab', tabs: 'tab', tablet: 'tab', tablets: 'tab',
  cap: 'cap', caps: 'cap', capsule: 'cap', capsules: 'cap',
  ml: 'ml', 'gtt': 'gtt', gtts: 'gtt', drop: 'gtt', drops: 'gtt',
  g: 'g',
  // Round 5, fix 4 (additive): common, unambiguous dose-unit synonyms.
  // "inhalation"/"inhalations" fold to the same canonical as puff (both
  // describe one actuation of an inhaler) — see ROUTE_MAP's doc for why
  // "inhalation" ALSO appears there as a route synonym: the two lookups
  // are independent, so a single token can carry both meanings, exactly
  // as casual sig shorthand does ("2 inhalations" is both a dose count
  // unit and an implicit inhaled route).
  tsp: 'tsp', teaspoon: 'tsp', teaspoons: 'tsp',
  tbsp: 'tbsp', tablespoon: 'tbsp', tablespoons: 'tbsp',
  oz: 'oz', ounce: 'oz', ounces: 'oz',
  puff: 'puff', puffs: 'puff', inhalation: 'puff', inhalations: 'puff',
  spray: 'spray', sprays: 'spray',
  unit: 'unit', units: 'unit',
  patch: 'patch', patches: 'patch'
};

const ROMAN_MAP: Record<string, number> = {
  i: 1, ii: 2, iii: 3, iv: 4, v: 5, vi: 6, vii: 7, viii: 8
};

/**
 * Spelled-out number words -> digits, for dose counts stated in prose
 * ("one tablet") instead of digits/roman numerals ("1 tablet"/"i tab").
 * Both directions (word or digit) must normalize to the SAME numeric
 * value so "1 tablet ... at bedtime" and "ONE TABLET ... AT BEDTIME"
 * compare as semantically identical.
 */
const NUMBER_WORD_MAP: Record<string, number> = {
  one: 1, two: 2, three: 3, four: 4, five: 5,
  six: 6, seven: 7, eight: 8, nine: 9, ten: 10
};

// Route/frequency/other terms that consist of a space (multi-word phrases)
// need to be checked before naive tokenization collapses them. Order
// matters: longer/more-specific phrases must be listed before shorter
// phrases they contain (e.g. "every night at bedtime" before "at
// bedtime" before "every night") so a global replace of the shorter
// phrase doesn't fire first and leave a partial match behind.
const MULTI_WORD_TERMS: Array<[RegExp, string]> = [
  [/\bby mouth\b/g, 'po'],
  [/\bas needed\b/g, 'prn'],
  [/\bevery other day\b/g, 'qod'],
  [/\b(every day|once a day|once daily|each day)\b/g, 'daily'],
  [/\btwice (a day|daily)\b/g, 'bid'],
  [/\bthree times (a day|daily)\b/g, 'tid'],
  [/\bfour times (a day|daily)\b/g, 'qid'],
  // "at bedtime"/"every night" family -> qhs (once nightly), same as the
  // existing qhs/hs abbreviations.
  [/\bevery night at bedtime\b/g, 'qhs'],
  [/\bat bedtime\b/g, 'qhs'],
  [/\bevery night\b/g, 'qhs'],
  // Morning/evening phrasings imply once-daily frequency, same as the
  // existing qam/qpm abbreviations (live report: source sig "...in the
  // morning...Once a day" vs entered "...EVERY MORNING." went YELLOW
  // sig_ambiguous because only one side had an explicit frequency word —
  // "every/each/in the morning" IS the frequency statement, not just
  // timing, so it must resolve to the same 1/day as "once a day"/"qam".
  //
  // REVIEW FIX (confirmed false GREEN): the negative lookahead below
  // refuses to fold when the phrase is followed by " and <word>" — e.g.
  // "every morning and evening" is a genuinely twice-daily instruction,
  // not once-daily. Without the lookahead, "every morning" folded to
  // qam and the trailing "and evening" was silently dropped (nothing in
  // FREQ_MAP matches a bare "evening"), so a real BID sig compared
  // GREEN against an entered "qam" sig. Blocking the fold here leaves
  // timesPerDay unextracted for that continuation form, which correctly
  // falls back to YELLOW sig_ambiguous — the same safe behavior this
  // input had before morning/evening folding was added at all.
  // Round 6, fix 7 (additive): tolerate the specific OCR confusable
  // "moming"/"evenmg" — OCR routinely misreads "rn" as "m" (and, in the
  // evening case, drops/misreads the trailing "in" as "m"), producing
  // "moming" for "morning" and "evenmg" for "evening". Scoped to exactly
  // these two literal misreadings inside this phrase alternation (never a
  // general fuzzy-match pass elsewhere in the sig) — the negative
  // lookahead guarding "and evening"/"and morning" continuations still
  // applies to both the correct and confusable spellings.
  [/\b(in the (?:morning|moming)|each (?:morning|moming)|every (?:morning|moming))\b(?!\s+and\b)/g, 'qam'],
  [/\b(in the (?:evening|evenmg)|each (?:evening|evenmg)|every (?:evening|evenmg))\b(?!\s+and\b)/g, 'qpm'],
  // Round 5, fix 4 (additive): substituted directly to the 'nasal' route
  // token (ROUTE_MAP), same pattern as "by mouth" -> 'po' above.
  [/\bin each nostril\b/g, 'nasal']
];

function preprocess(raw: string): string {
  let s = raw.toLowerCase().trim();
  for (const [re, replacement] of MULTI_WORD_TERMS) {
    s = s.replace(re, replacement);
  }
  // Normalize common punctuation variants of abbreviations away, but
  // keep decimal points in numbers.
  s = s.replace(/q\.o\.d\./g, 'qod').replace(/q\.d\./g, 'qd').replace(/b\.i\.d\./g, 'bid').replace(/t\.i\.d\./g, 'tid').replace(/q\.i\.d\./g, 'qid');
  s = s.replace(/p\.o\./g, 'po').replace(/p\.r\.n\./g, 'prn');
  s = s.replace(/["“”]/g, '');
  return s;
}

/** Parse a duration token like "x7d", "x 10 days", "for 30 days" -> days. */
function extractDuration(s: string): number | null {
  let m = /x\s*(\d+)\s*d(ays)?\b/.exec(s);
  if (m) return Number(m[1]);
  m = /for\s+(\d+)\s*day(s)?\b/.exec(s);
  if (m) return Number(m[1]);
  return null;
}

function extractDoseCount(tokens: string[]): { count: number | null; consumedIdx: number } {
  for (let i = 0; i < tokens.length; i++) {
    const tok = tokens[i] ?? '';
    if (/^\d+(\.\d+)?$/.test(tok)) {
      return { count: Number(tok), consumedIdx: i };
    }
    if (NUMBER_WORD_MAP[tok] !== undefined) {
      return { count: NUMBER_WORD_MAP[tok] as number, consumedIdx: i };
    }
    if (ROMAN_MAP[tok] !== undefined) {
      // REVIEWER BLOCKER FIX (round 5, fix 4 hardening): "iv" collides
      // with ROUTE_MAP's intravenous abbreviation — before this gate,
      // extractDoseCount and extractRoute ran as two fully independent
      // scans, so a bare "iv" was read as BOTH doseCount=4 (roman
      // numeral) AND route='iv' simultaneously, on every sig containing
      // it. That produced a confirmed false GREEN: 'give iv daily'
      // (genuinely route=IV, no dose count at all) compared as
      // structurally identical to 'take iv tablets daily' (genuinely
      // doseCount=4 "iv tablets", no route) because BOTH sides
      // coincidentally resolved to the exact same {doseCount:4,
      // route:'iv'} pair.
      //
      // Every OTHER roman numeral (i/ii/iii/v/vi/vii/viii) has no
      // ROUTE_MAP collision at all and is accepted unconditionally,
      // unchanged from before. Only for "iv" specifically: it's accepted
      // as a roman-numeral dose count ONLY when the very next token is a
      // recognized dose-unit word ("iv tablets", "iv caps") — a roman
      // numeral with nothing to quantify is far more likely to be the
      // route. When the adjacency check fails, this candidate is
      // skipped (not consumed) so extractRoute's own scan below picks it
      // up as a route instead.
      if (ROUTE_MAP[tok] !== undefined) {
        const next = tokens[i + 1];
        if (!next || !DOSE_UNIT_MAP[next]) continue;
      }
      return { count: ROMAN_MAP[tok] as number, consumedIdx: i };
    }
  }
  return { count: null, consumedIdx: -1 };
}

function extractDoseUnit(tokens: string[]): string | null {
  for (const tok of tokens) {
    if (DOSE_UNIT_MAP[tok]) return DOSE_UNIT_MAP[tok] as string;
  }
  return null;
}

/**
 * REVIEWER BLOCKER FIX (round 5, fix 4 hardening): `excludeIdx` is the
 * token index extractDoseCount actually consumed (see its doc) —
 * skipped here so the SAME token can never be double-read as both a
 * roman-numeral dose count AND a route (the "iv" collision). Every
 * other caller of route extraction (there's only ever the one, from
 * parseSig) always passes doseCount's real consumedIdx, so this is not
 * an optional safety net — it's load-bearing for every sig, not just
 * ones containing "iv".
 */
function extractRoute(tokens: string[], excludeIdx: number = -1): string | null {
  for (let i = 0; i < tokens.length; i++) {
    if (i === excludeIdx) continue;
    const tok = tokens[i] as string;
    if (ROUTE_MAP[tok]) return ROUTE_MAP[tok] as string;
  }
  return null;
}

function extractFrequency(tokens: string[]): number | null {
  for (const tok of tokens) {
    if (FREQ_MAP[tok] !== undefined) return FREQ_MAP[tok] as number;
  }
  return null;
}

function extractPrn(tokens: string[]): boolean {
  return tokens.some((t) => PRN_TOKENS.has(t));
}

/** Round 5, fix 4 (additive) — see MEAL_RELATION_MAP's doc. */
function extractMealRelation(tokens: string[]): string | null {
  for (const tok of tokens) {
    if (MEAL_RELATION_MAP[tok]) return MEAL_RELATION_MAP[tok] as string;
  }
  return null;
}

/**
 * Parse a sig string into structured components. Best-effort: fields we
 * can't find are left null. `ambiguous` is set true when we can't find
 * enough of the core triad (dose count, route, frequency) to be
 * confident in a structural comparison.
 */
export function parseSig(raw: string): ParsedSig {
  const pre = preprocess(raw);
  const durationDays = extractDuration(pre);
  const tokens = pre
    .replace(/x\s*\d+\s*d(ays)?/g, '')
    .replace(/for\s+\d+\s*days?/g, '')
    .split(/[\s,]+/)
    .map((t) => t.replace(/[.]+$/, ''))
    .filter(Boolean);

  const { count: doseCount, consumedIdx } = extractDoseCount(tokens);
  const doseUnit = extractDoseUnit(tokens);
  // REVIEWER BLOCKER FIX (round 5, fix 4 hardening): pass doseCount's
  // consumed token index so the same token can never double as both a
  // roman-numeral dose count and a route — see extractRoute's doc.
  const route = extractRoute(tokens, consumedIdx);
  const timesPerDay = extractFrequency(tokens);
  const prn = extractPrn(tokens);
  const mealRelation = extractMealRelation(tokens);

  // Detect frequency-LOOKING tokens we don't recognize (e.g. "q5h",
  // a misspelled "qhd"). These make the frequency indeterminate.
  const hasUnrecognizedFreqToken = tokens.some(
    (t) =>
      /^q[a-z0-9]+$/.test(t) &&
      FREQ_MAP[t] === undefined &&
      ROUTE_MAP[t] === undefined
  );

  // Ambiguous if we're missing dose count AND route AND frequency —
  // i.e. we extracted essentially nothing structural. mealRelation is
  // deliberately NOT part of this core triad (same treatment as
  // doseUnit/durationDays/prn above it) — it's an optional qualifier
  // layered on top, not itself enough to call a sig "structurally
  // parsed".
  const foundCount = [doseCount, route, timesPerDay].filter((v) => v !== null).length;
  const ambiguous = foundCount === 0;

  return {
    doseCount,
    doseUnit,
    route,
    timesPerDay,
    prn,
    durationDays,
    mealRelation,
    ambiguous,
    hasUnrecognizedFreqToken
  };
}

export function compareSigs(
  sourceRaw: string | null | undefined,
  enteredRaw: string | null | undefined
): SigCompareResult {
  const sourceEmpty = !sourceRaw || !sourceRaw.trim();
  const enteredEmpty = !enteredRaw || !enteredRaw.trim();

  if (sourceEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription did not provide sig/directions to compare.'
    };
  }
  if (enteredEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'No sig/directions were entered in PioneerRx to compare against the source.'
    };
  }

  // Round 6, fix 1 (highest priority): IDENTICAL-SIG FAST PATH, checked
  // BEFORE any structured parsing. Two sigs that are the same text after
  // only trivial normalization (case, whitespace, trailing punctuation)
  // are definitionally a match — whether or not this engine's structured
  // parser can extract dose/route/frequency from that text is irrelevant
  // when both sides state the exact same instructions verbatim. Without
  // this, a sig neither side's parser can structure (e.g. "Inject 12.5mg
  // under the skin every week.") fell to yellow sig_ambiguous even when
  // literally identical on both sides.
  const trivialNormalize = (s: string) =>
    s
      .toLowerCase()
      .trim()
      .replace(/\s+/g, ' ')
      .replace(/[.,;:!]+$/, '')
      .trim();
  if (trivialNormalize(sourceRaw) === trivialNormalize(enteredRaw)) {
    return {
      status: 'green',
      reasonCode: 'verbatim_match',
      explanation: 'Sig text is identical after case/whitespace/trailing-punctuation normalization — an exact verbatim match, regardless of whether it could be structurally parsed.'
    };
  }

  const a = parseSig(sourceRaw);
  const b = parseSig(enteredRaw);

  if (a.ambiguous || b.ambiguous) {
    return {
      status: 'yellow',
      reasonCode: 'sig_ambiguous',
      explanation: `Could not confidently parse structured dose/route/frequency from one or both sigs ("${sourceRaw}" / "${enteredRaw}"); needs human review.`
    };
  }

  if (a.hasUnrecognizedFreqToken || b.hasUnrecognizedFreqToken) {
    return {
      status: 'yellow',
      reasonCode: 'sig_ambiguous',
      explanation: `One or both sigs contain a frequency-like token this engine does not recognize ("${sourceRaw}" / "${enteredRaw}"); the frequency comparison is indeterminate — needs human review.`
    };
  }

  // Compare each extracted component. Safety rule: if one side parsed a
  // component and the other side did not, that component's comparison is
  // INDETERMINATE — the answer is YELLOW, never a silent skip to green.
  // (Exception per spec: dose unit missing on one side carries no
  // penalty — sigs routinely omit "tab"/"cap" without ambiguity.)
  const mismatches: string[] = [];
  const indeterminate: string[] = [];

  const checkComponent = (label: string, va: number | string | null, vb: number | string | null) => {
    if (va !== null && vb !== null) {
      if (va !== vb) mismatches.push(`${label} ${va} vs ${vb}`);
    } else if (va !== null || vb !== null) {
      indeterminate.push(label);
    }
  };

  checkComponent('dose count', a.doseCount, b.doseCount);
  checkComponent('route', a.route, b.route);
  checkComponent('frequency (per day)', a.timesPerDay, b.timesPerDay);
  checkComponent('duration (days)', a.durationDays, b.durationDays);
  // Round 5, fix 4 (additive): meal-timing qualifier ("ac" vs "pc") — same
  // indeterminate-if-only-one-side-specifies treatment as the rest of
  // this core-ish set (see MEAL_RELATION_MAP's doc for why it's not part
  // of the ambiguity triad, but IS still compared once both sides parse).
  checkComponent('meal timing (before/after meals)', a.mealRelation, b.mealRelation);

  // Dose unit: both present and different (tab vs cap = different dose
  // forms) = contradiction; one side missing = no penalty.
  if (a.doseUnit !== null && b.doseUnit !== null && a.doseUnit !== b.doseUnit) {
    mismatches.push(`dose unit ${a.doseUnit} vs ${b.doseUnit}`);
  }

  if (a.prn !== b.prn) {
    mismatches.push(`PRN flag ${a.prn} vs ${b.prn}`);
  }

  if (mismatches.length > 0) {
    return {
      status: 'red',
      reasonCode: 'sig_mismatch',
      explanation: `Sig instructions contradict after expansion: ${mismatches.join('; ')}.`
    };
  }

  if (indeterminate.length > 0) {
    return {
      status: 'yellow',
      reasonCode: 'sig_ambiguous',
      explanation: `Only one side specifies ${indeterminate.join(', ')} — the comparison for ${indeterminate.length === 1 ? 'that component' : 'those components'} is indeterminate; needs human review.`
    };
  }

  return {
    status: 'green',
    reasonCode: 'exact_match',
    explanation: 'Sig instructions are semantically equal after abbreviation expansion.'
  };
}
