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

/**
 * Round 5, fix 3 — live report: some e-scripts show an "Available:" date
 * (seen on refill-response layouts); PioneerRx then displays THAT date —
 * not the Written date — in its own entered-date fields, so a
 * technician's entered date naturally ends up matching Available, not
 * Written. Before this, the entered date was only ever compared against
 * source.dateWritten, so a script with an Available date and no Written
 * date always fell through to compareDates' "source date not provided"
 * yellow, even though the entered date was, in fact, correct.
 *
 * Used for the 'dateWritten' verdict field itself (folds Available-
 * awareness into the existing field rather than adding a second,
 * independently-scored comparison) — see engine/index.ts. The separate,
 * purely-informational 'availableDate' verdict row (added conditionally
 * in engine/index.ts, only when source.availableDate is present) mirrors
 * whatever this function returns, so the overlay can show WHY the written
 * date shows green/red next to the actual Available value it was checked
 * against.
 *
 * Decision order:
 *  1. No availableDate at all -> behavior fully unchanged (plain
 *     compareDates against dateWritten).
 *  2. availableDate present and the entered date matches it -> GREEN,
 *     'available_date_match' (regardless of whether dateWritten is also
 *     present/matches — Available is PioneerRx's displayed date, so a
 *     match against it is always the strongest signal).
 *  3. availableDate present but entered doesn't match it, AND there's no
 *     Written date to fall back to -> surface the Available comparison's
 *     own result as-is (its usual not_provided/unparseable/date_mismatch
 *     status).
 *  4. Both Written and Available are present, entered matches Available
 *     status is not green, but DOES match Written -> that's still a
 *     legitimate match; return the Written comparison as-is.
 *  5. Both present, entered matches NEITHER (and both sides parsed
 *     cleanly enough to make that comparison meaningful) -> explicit RED
 *     'date_mismatch' per the branch brief ("entered matches neither ⇒
 *     RED"), never a silent yellow — a real discrepancy, not a missing
 *     field.
 */
export function compareWrittenOrAvailableDate(
  sourceWritten: string | null | undefined,
  sourceAvailable: string | null | undefined,
  enteredRaw: string | null | undefined,
  opts?: DateParseOptions
): DateCompareResult {
  const availableEmpty = !sourceAvailable || !sourceAvailable.trim();
  if (availableEmpty) {
    return compareDates(sourceWritten, enteredRaw, opts);
  }

  const availableResult = compareDates(sourceAvailable, enteredRaw, opts);
  if (availableResult.status === 'green') {
    const parsedAvailable = parseDate(sourceAvailable, opts);
    return {
      status: 'green',
      reasonCode: 'available_date_match',
      explanation: `Source shows an Available date (${parsedAvailable ?? sourceAvailable}); PioneerRx displays this instead of the Written date, and the entered date matches it.`
    };
  }
  // Entered date itself is the issue (missing/unparseable) — preserve
  // that signal as-is rather than manufacturing a mismatch against a
  // Written date the entered value couldn't even be compared to.
  if (availableResult.status === 'yellow') return availableResult;

  const writtenEmpty = !sourceWritten || !sourceWritten.trim();
  if (writtenEmpty) return availableResult;

  const writtenResult = compareDates(sourceWritten, enteredRaw, opts);
  if (writtenResult.status === 'green') return writtenResult;

  return {
    status: 'red',
    reasonCode: 'date_mismatch',
    explanation: `Entered date matches neither the source's Written date nor its Available date.`
  };
}
