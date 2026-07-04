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
  ou: 'ou' // both eyes
};

/** Frequency abbreviations -> times per day. */
const FREQ_MAP: Record<string, number> = {
  qd: 1, 'q.d.': 1, daily: 1, qam: 1, qpm: 1, qhs: 1, hs: 1,
  qod: 0.5, 'q.o.d.': 0.5, // every other day
  bid: 2, 'b.i.d.': 2, 'twice daily': 2, 'twice a day': 2,
  tid: 3, 't.i.d.': 3, 'three times daily': 3, 'three times a day': 3,
  qid: 4, 'q.i.d.': 4, 'four times daily': 4, 'four times a day': 4,
  q4h: 6, q6h: 4, q8h: 3, q12h: 2
};

const PRN_TOKENS = new Set(['prn', 'p.r.n.', 'as needed']);

const DOSE_UNIT_MAP: Record<string, string> = {
  tab: 'tab', tabs: 'tab', tablet: 'tab', tablets: 'tab',
  cap: 'cap', caps: 'cap', capsule: 'cap', capsules: 'cap',
  ml: 'ml', 'gtt': 'gtt', gtts: 'gtt', drop: 'gtt', drops: 'gtt',
  g: 'g'
};

const ROMAN_MAP: Record<string, number> = {
  i: 1, ii: 2, iii: 3, iv: 4, v: 5, vi: 6, vii: 7, viii: 8
};

// Route/frequency/other terms that consist of a space (multi-word phrases)
// need to be checked before naive tokenization collapses them.
const MULTI_WORD_TERMS: Array<[RegExp, string]> = [
  [/\bby mouth\b/g, 'po'],
  [/\bas needed\b/g, 'prn'],
  [/\bevery other day\b/g, 'qod'],
  [/\b(every day|once a day|once daily|each day)\b/g, 'daily'],
  [/\btwice (a day|daily)\b/g, 'bid'],
  [/\bthree times (a day|daily)\b/g, 'tid'],
  [/\bfour times (a day|daily)\b/g, 'qid']
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
    if (ROMAN_MAP[tok] !== undefined) {
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

function extractRoute(tokens: string[]): string | null {
  for (const tok of tokens) {
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

  const { count: doseCount } = extractDoseCount(tokens);
  const doseUnit = extractDoseUnit(tokens);
  const route = extractRoute(tokens);
  const timesPerDay = extractFrequency(tokens);
  const prn = extractPrn(tokens);

  // Detect frequency-LOOKING tokens we don't recognize (e.g. "q5h",
  // a misspelled "qhd"). These make the frequency indeterminate.
  const hasUnrecognizedFreqToken = tokens.some(
    (t) =>
      /^q[a-z0-9]+$/.test(t) &&
      FREQ_MAP[t] === undefined &&
      ROUTE_MAP[t] === undefined
  );

  // Ambiguous if we're missing dose count AND route AND frequency —
  // i.e. we extracted essentially nothing structural.
  const foundCount = [doseCount, route, timesPerDay].filter((v) => v !== null).length;
  const ambiguous = foundCount === 0;

  return { doseCount, doseUnit, route, timesPerDay, prn, durationDays, ambiguous, hasUnrecognizedFreqToken };
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
