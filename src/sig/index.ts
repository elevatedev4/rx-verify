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
  /**
   * Round 7, fix 1 (additive): set of TIME_OF_DAY_MAP canonical ids found
   * in the sig (e.g. ['morning'], ['lunch'], ['morning', 'lunch']), sorted
   * for stable set-equality comparison, or null if none were found. See
   * TIME_OF_DAY_MAP's doc for why "lunch" and "noon" are DIFFERENT ids
   * even though both are common once-daily timings — that distinction is
   * this branch's whole fix.
   */
  timeOfDay: string[] | null;
  /**
   * Round 7, fix 1 (additive): tokens left over after every known
   * extractor (dose count/unit, route, frequency, prn, meal relation,
   * time-of-day) and FILLER_WORDS have had a chance to claim them. A
   * non-empty, ASYMMETRIC residual (one side has leftover text the other
   * side lacks) is the general form of this branch's bug: real content
   * ("at lunch time", a second dose count, a glued duration) silently
   * vanishing before comparison instead of blocking a false green. See
   * compareSigs' residual-guard for how this is used.
   */
  residualTokens: string[];
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

/**
 * Round 7, fix 1: TIME_OF_DAY concept — the false-GREEN this branch fixes.
 * Live report: source sig said "...at lunch time..." and the entered sig
 * said "...at noon.". Neither "lunch" nor "noon" was in ANY table this
 * file consulted, so both were silently dropped before comparison ever
 * saw them — dose count/route/unit happened to otherwise match, and the
 * result was a false GREEN "exact_match" on two sigs that genuinely
 * disagree about when the dose is taken. Lunchtime and noon are not the
 * same thing (pharmacist-owner's ruling): lunch is a meal (clock time
 * varies by patient/day), noon is a fixed clock time.
 *
 * Keys land here from two sources, deliberately merged into ONE table so
 * comparison never has to know which fired:
 *  - the qam/qpm/qhs/hs frequency-abbreviation tokens (FREQ_MAP above) —
 *    these already exist as single tokens once MULTI_WORD_TERMS folds
 *    "in the morning" / "in the evening" / "at bedtime" etc, or appear
 *    verbatim in the sig as-is;
 *  - bare literal words (morning/evening/bedtime/night, plus their OCR
 *    confusables moming/evenmg — see fix 7's doc) for when that SAME
 *    MULTI_WORD_TERMS fold is deliberately blocked by its "and <word>"
 *    continuation guard: the FREQUENCY inference is correctly withheld
 *    in that case (it might be BID, not once-daily), but the time-of-day
 *    CONCEPT itself is still real and must still be captured, not
 *    silently dropped along with the blocked fold.
 *  - new bare words (noon/midday/lunch/afternoon/breakfast/dinner/
 *    supper) with no existing frequency abbreviation of their own; the
 *    matching MULTI_WORD_TERMS entries below fold away the surrounding
 *    preposition ("at noon" -> "noon") the same way "by mouth" -> "po"
 *    does, but the bare word is ALSO a direct key here so a sig that
 *    omits the preposition still resolves.
 *
 * `id` is the actual comparison key — two sigs agree on time-of-day only
 * when their extracted id SETS are equal. `category` documents *why*
 * ids never collide across the meal/clock boundary: every meal-anchored
 * id (breakfast/lunch/dinner) and every clock-anchored id (morning/noon/
 * afternoon/evening/bedtime) is already a distinct string, so ordinary
 * set equality enforces "lunch != noon" for free — no special-case code
 * needed. ac/pc meal-RELATION qualifiers (MEAL_RELATION_MAP above) are a
 * separate, pre-existing concept and untouched by this table.
 *
 * "noon" and "midday" are deliberately the SAME id — genuinely the same
 * fixed clock time. "dinner" and "supper" are likewise the same id
 * (regional names for the same meal). "lunch" is deliberately its OWN
 * id, distinct from both — see this table's opening paragraph.
 */
const TIME_OF_DAY_MAP: Record<string, { id: string; category: 'clock' | 'meal' }> = {
  qam: { id: 'morning', category: 'clock' },
  morning: { id: 'morning', category: 'clock' },
  moming: { id: 'morning', category: 'clock' },
  qpm: { id: 'evening', category: 'clock' },
  evening: { id: 'evening', category: 'clock' },
  evenmg: { id: 'evening', category: 'clock' },
  qhs: { id: 'bedtime', category: 'clock' },
  hs: { id: 'bedtime', category: 'clock' },
  bedtime: { id: 'bedtime', category: 'clock' },
  night: { id: 'bedtime', category: 'clock' },
  noon: { id: 'noon', category: 'clock' },
  midday: { id: 'noon', category: 'clock' },
  afternoon: { id: 'afternoon', category: 'clock' },
  lunch: { id: 'lunch', category: 'meal' },
  lunchtime: { id: 'lunch', category: 'meal' },
  breakfast: { id: 'breakfast', category: 'meal' },
  dinner: { id: 'dinner', category: 'meal' },
  supper: { id: 'dinner', category: 'meal' }
};

/**
 * Round 7, fix 1: connector/instructional words that carry no dose/route/
 * frequency/time-of-day meaning on their own, so their presence on only
 * ONE side of a comparison must not, by itself, block a green verdict
 * (see the residual-guard in compareSigs). Deliberately a SHORT list —
 * every entry here was required to keep an existing, already-verified-
 * correct GREEN test passing (e.g. "2 tab po bid" vs "Take two tablets
 * by mouth twice daily." — "take" appears only on the worded side). The
 * bar for adding a new entry is the same: it must be a word that adds no
 * clinical information, never a word that merely happens to be common.
 * When in doubt, leave it OUT — an unnecessary residual token only ever
 * costs an extra YELLOW (safe); a wrongly-filtered one could hide a real
 * difference (the exact failure class this branch fixes).
 */
const FILLER_WORDS = new Set(['take', 'by']);

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
  [/\bin each nostril\b/g, 'nasal'],
  // Round 7, fix 1 (additive): TIME_OF_DAY concept — same pattern as "by
  // mouth" -> "po" above, folding away the surrounding preposition onto
  // a single canonical TIME_OF_DAY_MAP key (see that table's doc). No
  // "and <word>" continuation guard is needed here the way morning/
  // evening above need one: these words don't imply a FREQUENCY (there's
  // no single numeric rate to get wrong), so capturing more than one —
  // "at noon and at lunch" -> both 'noon' and 'lunch' present — is
  // correct, not a hazard. Longer/more-specific phrases first, same
  // ordering rule as the rest of this list.
  [/\bat lunch time\b/g, 'lunch'],
  [/\blunch time\b/g, 'lunch'],
  [/\bat lunch\b/g, 'lunch'],
  [/\bwith lunch\b/g, 'lunch'],
  [/\bat midday\b/g, 'noon'],
  [/\bat noon\b/g, 'noon'],
  [/\bin the afternoon\b/g, 'afternoon'],
  [/\beach afternoon\b/g, 'afternoon'],
  [/\bevery afternoon\b/g, 'afternoon'],
  [/\bat breakfast\b/g, 'breakfast'],
  [/\bwith breakfast\b/g, 'breakfast'],
  [/\bat dinner\b/g, 'dinner'],
  [/\bwith dinner\b/g, 'dinner'],
  [/\bat supper\b/g, 'dinner'],
  [/\bwith supper\b/g, 'dinner']
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
 * Round 7, fix 1 (additive) — see TIME_OF_DAY_MAP's doc. Unlike the other
 * extractors above, this collects a SET (a sig can name more than one
 * time-of-day, e.g. "at noon and at lunch"), returned sorted for stable
 * equality comparison in compareSigs.
 */
function extractTimeOfDay(tokens: string[]): string[] | null {
  const found = new Set<string>();
  for (const tok of tokens) {
    const entry = TIME_OF_DAY_MAP[tok];
    if (entry) found.add(entry.id);
  }
  return found.size > 0 ? Array.from(found).sort() : null;
}

/**
 * Round 7, fix 1 (additive): is this token accounted for by SOME known
 * vocabulary (dose count/unit, route, frequency, prn, meal relation,
 * time-of-day) or the FILLER_WORDS stoplist? Deliberately checks
 * vocabulary MEMBERSHIP, not which specific extractor actually consumed
 * this exact token instance — a sig that repeats a word already used
 * elsewhere (e.g. "daily" appearing after an earlier "qam" already set
 * the frequency) is redundant, not unrecognized, and must not be flagged
 * as residual. See extractResidualTokens' doc for how this is used.
 */
function isKnownSigToken(tok: string): boolean {
  if (FILLER_WORDS.has(tok)) return true;
  if (/^\d+(\.\d+)?$/.test(tok)) return true;
  if (NUMBER_WORD_MAP[tok] !== undefined) return true;
  if (ROMAN_MAP[tok] !== undefined) return true;
  if (DOSE_UNIT_MAP[tok]) return true;
  if (ROUTE_MAP[tok]) return true;
  if (FREQ_MAP[tok] !== undefined) return true;
  if (PRN_TOKENS.has(tok)) return true;
  if (MEAL_RELATION_MAP[tok]) return true;
  if (TIME_OF_DAY_MAP[tok]) return true;
  return false;
}

/**
 * Round 7, fix 1 (additive): the broader guard behind this branch's fix,
 * beyond the specific TIME_OF_DAY concept above. Any token that survives
 * tokenization without being claimed by a known extractor OR the filler
 * stoplist is "residual" — real text this engine could not classify.
 * compareSigs uses this as a final safety net: a GREEN verdict requires
 * both sides' residual sets to match (usually both empty), so leftover
 * content on only one side (a second dose count, unrecognized OCR glue,
 * an unparseable time phrase) can never be silently dropped on the way
 * to a false GREEN — see compareSigs' residual-guard comment.
 */
function extractResidualTokens(tokens: string[]): string[] {
  return tokens.filter((t) => !isKnownSigToken(t));
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
  // Round 7, fix 1 (additive) — see TIME_OF_DAY_MAP's doc.
  const timeOfDay = extractTimeOfDay(tokens);
  const residualTokens = extractResidualTokens(tokens);

  // Detect frequency-LOOKING tokens we don't recognize (e.g. "q5h",
  // a misspelled "qhd"). These make the frequency indeterminate.
  const hasUnrecognizedFreqToken = tokens.some(
    (t) =>
      /^q[a-z0-9]+$/.test(t) &&
      FREQ_MAP[t] === undefined &&
      ROUTE_MAP[t] === undefined
  );

  // Ambiguous if we're missing dose count AND route AND frequency AND
  // time-of-day — i.e. we extracted essentially nothing structural.
  // mealRelation is deliberately NOT part of this set (same treatment as
  // doseUnit/durationDays/prn above it) — it's an optional qualifier
  // layered on top, not itself enough to call a sig "structurally
  // parsed". timeOfDay, added in round 7 fix 1, IS included: a sig that
  // says nothing but "at noon" is fully structured for this engine's
  // purposes (it names a real, comparable instruction), unlike a
  // qualifier that only ever rides along with other structure.
  const foundCount =
    [doseCount, route, timesPerDay].filter((v) => v !== null).length +
    (timeOfDay && timeOfDay.length > 0 ? 1 : 0);
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
    hasUnrecognizedFreqToken,
    timeOfDay,
    residualTokens
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

  // Round 7, fix 1 (additive): TIME_OF_DAY concept — see TIME_OF_DAY_MAP's
  // doc for the live false-GREEN this fixes ("at lunch time" vs "at
  // noon"). Compared as SETS (order-independent) rather than through the
  // scalar checkComponent above: a sig can legitimately name more than
  // one time-of-day ("at noon and at lunch"), and set equality is what
  // makes "lunch != noon" fall out automatically from the ids being
  // distinct strings — same graded treatment as every other structural
  // mismatch here (both present and different = contradiction/RED; one
  // side has it and the other doesn't = indeterminate/YELLOW).
  const aTod = a.timeOfDay ?? [];
  const bTod = b.timeOfDay ?? [];
  if (aTod.length > 0 && bTod.length > 0) {
    const sameSet = aTod.length === bTod.length && aTod.every((id, i) => id === bTod[i]);
    if (!sameSet) {
      mismatches.push(`time of day [${aTod.join(', ')}] vs [${bTod.join(', ')}]`);
    }
  } else if (aTod.length > 0 || bTod.length > 0) {
    indeterminate.push('time of day');
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

  // Round 7, fix 1 (additive): broader residual-token guard, the general
  // form of this branch's bug. Every structural component above matched
  // (or both sides omitted it), but that alone isn't enough to call two
  // sigs equal if one side has leftover text — a second dose instruction,
  // unrecognized OCR glue, an unparseable time phrase — that the other
  // side simply doesn't have. Silently ignoring that leftover is exactly
  // how "amand"/"for30days"/an unrecognized time word produced the live
  // false GREEN. Symmetric (shared) leftover words — e.g. both sides
  // saying "take" — are NOT a difference and must not block green; only
  // an ASYMMETRIC residual (present on one side, absent on the other) is
  // graded, consistent with how every other component above treats
  // one-sided information as indeterminate rather than a hard mismatch.
  const aResidual = new Set(a.residualTokens);
  const bResidual = new Set(b.residualTokens);
  const residualDiff = [
    ...a.residualTokens.filter((t) => !bResidual.has(t)),
    ...b.residualTokens.filter((t) => !aResidual.has(t))
  ];
  if (residualDiff.length > 0) {
    return {
      status: 'yellow',
      reasonCode: 'sig_ambiguous',
      explanation: `One or both sigs contain leftover text this engine could not classify (${Array.from(new Set(residualDiff)).join(', ')}) that isn't present on both sides; a silent drop here could hide a real difference — needs human review.`
    };
  }

  return {
    status: 'green',
    reasonCode: 'exact_match',
    explanation: 'Sig instructions are semantically equal after abbreviation expansion.'
  };
}
