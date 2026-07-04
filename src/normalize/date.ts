/**
 * Date normalization + comparison for DOB / date-written fields.
 *
 * Supported input formats:
 *  - MM/DD/YYYY, M/D/YYYY
 *  - M/D/YY (2-digit year; windowed: 00-49 -> 2000s, 50-99 -> 1900s)
 *  - YYYY-MM-DD (ISO)
 *  - "Jul 2, 2026" / "July 2, 2026" style month names
 *
 * All parse successfully to an ISO "YYYY-MM-DD" string internally.
 *
 * Verdict philosophy: dates have no legitimate-difference category once
 * both sides provide a value — a DOB or date-written either matches or it
 * doesn't. Exact = GREEN. Both present and differ = RED. Source absent =
 * YELLOW not_provided.
 */

export type DateCompareStatus = 'green' | 'yellow' | 'red';

export interface DateCompareResult {
  status: DateCompareStatus;
  reasonCode: string;
  explanation: string;
}

const MONTH_NAMES: Record<string, number> = {
  jan: 1, january: 1,
  feb: 2, february: 2,
  mar: 3, march: 3,
  apr: 4, april: 4,
  may: 5,
  jun: 6, june: 6,
  jul: 7, july: 7,
  aug: 8, august: 8,
  sep: 9, sept: 9, september: 9,
  oct: 10, october: 10,
  nov: 11, november: 11,
  dec: 12, december: 12
};

function pad2(n: number): string {
  return n < 10 ? `0${n}` : `${n}`;
}

export interface DateParseOptions {
  /**
   * The date being parsed cannot be in the future (e.g. a DOB). When a
   * 2-digit year windows into a future year, re-window it to the 1900s
   * instead — "3/5/45" as a DOB means 1945, not 2045.
   */
  pastOnly?: boolean;
  /** Reference year for pastOnly windowing; defaults to the current year. */
  referenceYear?: number;
}

function windowYear(twoDigit: number, opts?: DateParseOptions): number {
  let year = twoDigit <= 49 ? 2000 + twoDigit : 1900 + twoDigit;
  if (opts?.pastOnly) {
    const refYear = opts.referenceYear ?? new Date().getFullYear();
    if (year > refYear) year -= 100;
  }
  return year;
}

/**
 * Parse a free-text date into an ISO "YYYY-MM-DD" string.
 * Returns null if the string cannot be confidently parsed.
 */
export function parseDate(raw: string, opts?: DateParseOptions): string | null {
  const s = raw.trim();
  if (!s) return null;

  // ISO: YYYY-MM-DD
  let m = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(s);
  if (m) {
    const year = Number(m[1]);
    const month = Number(m[2]);
    const day = Number(m[3]);
    if (isValidYMD(year, month, day)) return `${m[1]}-${pad2(month)}-${pad2(day)}`;
    return null;
  }

  // MM/DD/YYYY or M/D/YYYY or M/D/YY
  m = /^(\d{1,2})\/(\d{1,2})\/(\d{2}|\d{4})$/.exec(s);
  if (m) {
    const month = Number(m[1]);
    const day = Number(m[2]);
    const rawYear = m[3] as string;
    const year = rawYear.length === 2 ? windowYear(Number(rawYear), opts) : Number(rawYear);
    if (isValidYMD(year, month, day)) return `${year}-${pad2(month)}-${pad2(day)}`;
    return null;
  }

  // "Jul 2, 2026" / "July 2, 2026" / "Jul 2 2026"
  m = /^([A-Za-z]+)\.?\s+(\d{1,2}),?\s+(\d{4})$/.exec(s);
  if (m) {
    const monthName = (m[1] ?? '').toLowerCase();
    const month = MONTH_NAMES[monthName];
    const day = Number(m[2]);
    const year = Number(m[3]);
    if (month && isValidYMD(year, month, day)) return `${year}-${pad2(month)}-${pad2(day)}`;
    return null;
  }

  return null;
}

function isValidYMD(year: number, month: number, day: number): boolean {
  if (month < 1 || month > 12) return false;
  if (day < 1 || day > 31) return false;
  const daysInMonth = new Date(year, month, 0).getDate();
  return day <= daysInMonth;
}

export function compareDates(
  sourceRaw: string | null | undefined,
  enteredRaw: string | null | undefined,
  opts?: DateParseOptions
): DateCompareResult {
  const sourceEmpty = !sourceRaw || !sourceRaw.trim();
  const enteredEmpty = !enteredRaw || !enteredRaw.trim();

  if (sourceEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription did not provide a date to compare.'
    };
  }
  if (enteredEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'No date was entered in PioneerRx to compare against the source.'
    };
  }

  const a = parseDate(sourceRaw, opts);
  const b = parseDate(enteredRaw, opts);

  if (!a || !b) {
    return {
      status: 'yellow',
      reasonCode: 'unparseable_date',
      explanation: `Could not confidently parse one or both dates ("${sourceRaw}" / "${enteredRaw}"); needs human review.`
    };
  }

  if (a === b) {
    return {
      status: 'green',
      reasonCode: 'exact_match',
      explanation: 'Dates match exactly after normalization.'
    };
  }

  return {
    status: 'red',
    reasonCode: 'date_mismatch',
    explanation: `Source date ${a} does not match entered date ${b}.`
  };
}
