/**
 * Name normalization + comparison.
 *
 * Verdict philosophy for names:
 *  - GREEN only on normalized-exact match (case/punctuation/order folded).
 *  - YELLOW for nickname/fuzzy equivalence (never green — a nickname is a
 *    legitimate difference a human should still notice) or when one side
 *    is missing (not_provided).
 *  - RED when surnames plainly contradict (different person).
 */

export type NameCompareStatus = 'green' | 'yellow' | 'red';

export interface NameCompareResult {
  status: NameCompareStatus;
  reasonCode: string;
  explanation: string;
}

/**
 * Nickname equivalence table: canonical formal name -> set of common
 * nickname variants (US English). Roughly 100 pairs. Comparison is
 * symmetric: any two names that map to the same canonical bucket are
 * considered nickname-equivalent.
 */
const NICKNAME_GROUPS: string[][] = [
  ['william', 'bill', 'billy', 'will', 'liam'],
  ['robert', 'bob', 'bobby', 'rob', 'robbie'],
  ['richard', 'rick', 'ricky', 'dick', 'rich'],
  ['james', 'jim', 'jimmy', 'jamie'],
  ['john', 'jack', 'johnny', 'jon'],
  ['margaret', 'peggy', 'meg', 'maggie', 'marge'],
  ['elizabeth', 'liz', 'beth', 'betty', 'eliza', 'lisa', 'libby'],
  ['katherine', 'kate', 'katie', 'kathy', 'kit', 'catherine'],
  ['jennifer', 'jen', 'jenny'],
  ['patricia', 'pat', 'patty', 'trish'],
  ['deborah', 'deb', 'debbie'],
  ['barbara', 'barb', 'babs'],
  ['susan', 'sue', 'susie'],
  ['dorothy', 'dot', 'dottie'],
  ['michael', 'mike', 'mikey', 'mick'],
  ['christopher', 'chris', 'topher'],
  ['matthew', 'matt'],
  ['anthony', 'tony'],
  ['charles', 'chuck', 'charlie', 'chas'],
  ['thomas', 'tom', 'tommy'],
  ['daniel', 'dan', 'danny'],
  ['joseph', 'joe', 'joey'],
  ['edward', 'ed', 'eddie', 'ted', 'teddy'],
  ['donald', 'don', 'donnie'],
  ['ronald', 'ron', 'ronnie'],
  ['kenneth', 'ken', 'kenny'],
  ['steven', 'steve', 'stevie', 'stephen'],
  ['gregory', 'greg'],
  ['timothy', 'tim', 'timmy'],
  ['jeffrey', 'jeff'],
  ['samuel', 'sam', 'sammy'],
  ['benjamin', 'ben', 'benny'],
  ['nathaniel', 'nate', 'nathan'],
  ['alexander', 'alex', 'al'],
  ['andrew', 'andy', 'drew'],
  ['nicholas', 'nick', 'nicky'],
  ['zachary', 'zach', 'zack'],
  ['david', 'dave', 'davey'],
  ['frank', 'francis', 'frankie'],
  ['walter', 'walt', 'wally'],
  ['albert', 'al', 'bert'],
  ['arthur', 'art', 'artie'],
  ['harold', 'harry', 'hal'],
  ['henry', 'hank'],
  ['lawrence', 'larry'],
  ['leonard', 'leo', 'len'],
  ['raymond', 'ray'],
  ['russell', 'russ'],
  ['stanley', 'stan'],
  ['vincent', 'vince', 'vinny'],
  ['gerald', 'gerry', 'jerry'],
  ['victoria', 'vicki', 'tori'],
  ['patricia', 'patty', 'trish', 'pat'],
  ['cynthia', 'cindy'],
  ['christine', 'chris', 'christy', 'tina'],
  ['jacqueline', 'jackie', 'jacki'],
  ['virginia', 'ginny', 'ginger'],
  ['theresa', 'terry', 'tess'],
  ['rebecca', 'becky', 'becca'],
  ['rachel', 'rae'],
  ['samantha', 'sam', 'sammy'],
  ['stephanie', 'steph'],
  ['veronica', 'ronnie', 'roni'],
  ['gabriela', 'gabby', 'gabriella'],
  ['isabella', 'bella', 'izzy'],
  ['sophia', 'sophie'],
  ['olivia', 'liv', 'livvy'],
  ['emily', 'em', 'emmy'],
  ['amanda', 'mandy'],
  ['angela', 'angie'],
  ['brenda', 'bren'],
  ['carolyn', 'carol', 'carrie'],
  ['diana', 'diane', 'di'],
  ['eleanor', 'ellie', 'nell', 'nora'],
  ['frances', 'fran', 'frannie'],
  ['gloria', 'glo'],
  ['helen', 'nell'],
  ['irene', 'renie'],
  ['janet', 'jan'],
  ['joan', 'joanie'],
  ['judith', 'judy'],
  ['karen', 'kari'],
  ['linda', 'lynn'],
  ['linda', 'lin'],
  ['maria', 'marie', 'mary'],
  ['martha', 'marty'],
  ['nancy', 'nan'],
  ['pamela', 'pam'],
  ['phyllis', 'phyl'],
  ['sandra', 'sandy'],
  ['sharon', 'shari'],
  ['sylvia', 'syl'],
  ['wanda', 'wandi'],
  ['yolanda', 'yoli'],
  ['gregory', 'grigor'],
  ['isaac', 'ike', 'zac'],
  ['jonathan', 'jon', 'jonny'],
  ['joshua', 'josh'],
  ['justin', 'jus'],
  ['philip', 'phil'],
  ['sebastian', 'seb'],
  ['theodore', 'theo', 'ted', 'teddy'],
  ['tobias', 'toby'],
  ['xavier', 'xavi'],
  ['abigail', 'abby'],
  ['alexandra', 'alex', 'sasha', 'lexi']
];

/**
 * A nickname can belong to MULTIPLE canonical names (e.g. "al" is short
 * for both Albert and Alexander, "sam" for Samuel and Samantha), so each
 * variant maps to the full SET of canonical buckets it appears in. Two
 * first names are nickname-equivalent if their canonical sets intersect.
 */
const NICKNAME_TO_CANONICALS: Map<string, Set<string>> = new Map();
for (const group of NICKNAME_GROUPS) {
  const canonical = group[0];
  if (!canonical) continue;
  for (const variant of group) {
    let set = NICKNAME_TO_CANONICALS.get(variant);
    if (!set) {
      set = new Set<string>();
      NICKNAME_TO_CANONICALS.set(variant, set);
    }
    set.add(canonical);
  }
}

/** Lowercase, strip punctuation, collapse whitespace. */
function foldCase(s: string): string {
  return s
    .toLowerCase()
    .replace(/[.'’]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * Parse a free-text name into { first, last, suffix } handling both
 * "Last, First" and "First Last" orderings. Leading titles (Dr, Mr,
 * Mrs, Ms, Prof) are stripped; trailing generational suffixes (Jr, Sr,
 * II-V) are captured into `suffix` rather than polluting the surname.
 * Hyphenated surnames are normalized to spaces so "Garcia-Lopez" and
 * "Garcia Lopez" compare equal; `lastParts` holds the individual
 * components for partial-overlap detection.
 */
export interface ParsedName {
  first: string;
  last: string;
  /** Hyphenation-tolerant surname parts, e.g. ["garcia", "lopez"]. */
  lastParts: string[];
  /** Generational suffix (jr, sr, ii, iii, iv, v) if present. */
  suffix: string | null;
}

const TITLES = new Set(['dr', 'mr', 'mrs', 'ms', 'prof']);
const GENERATIONAL_SUFFIXES = new Set(['jr', 'sr', 'ii', 'iii', 'iv', 'v']);

/** Remove leading title tokens; pull trailing suffix tokens out. */
function stripTitlesAndSuffix(tokens: string[]): { tokens: string[]; suffix: string | null } {
  let start = 0;
  while (start < tokens.length - 1 && TITLES.has(tokens[start] ?? '')) start++;
  let end = tokens.length;
  let suffix: string | null = null;
  while (end - start > 1 && GENERATIONAL_SUFFIXES.has(tokens[end - 1] ?? '')) {
    suffix = tokens[end - 1] ?? null;
    end--;
  }
  return { tokens: tokens.slice(start, end), suffix };
}

export function parseName(raw: string): ParsedName {
  const folded = foldCase(raw);
  let first = '';
  let last = '';
  let suffix: string | null = null;

  if (folded.includes(',')) {
    const [lastPartRaw, firstPartRaw] = folded.split(',').map((p) => p.trim());
    const lastStripped = stripTitlesAndSuffix((lastPartRaw ?? '').split(/[\s]+/).filter(Boolean));
    const firstStripped = stripTitlesAndSuffix((firstPartRaw ?? '').split(/[\s]+/).filter(Boolean));
    last = lastStripped.tokens.join(' ');
    first = firstStripped.tokens[0] ?? '';
    suffix = lastStripped.suffix ?? firstStripped.suffix;
  } else {
    const stripped = stripTitlesAndSuffix(folded.split(' ').filter(Boolean));
    const parts = stripped.tokens;
    suffix = stripped.suffix;
    if (parts.length >= 2) {
      first = parts[0] ?? '';
      // Everything after the first token is treated as the (possibly
      // compound) surname; hyphens were folded to compare against
      // space-separated compound surnames.
      last = parts.slice(1).join(' ');
    } else {
      first = parts[0] ?? '';
      last = '';
    }
  }

  first = first.replace(/-/g, ' ').split(' ')[0] ?? first;
  last = last.replace(/-/g, ' ');

  const lastParts = last
    .split(/[\s]+/)
    .map((p) => p.trim())
    .filter(Boolean);

  return { first, last, lastParts, suffix };
}

function canonicalsOf(first: string): Set<string> {
  return NICKNAME_TO_CANONICALS.get(first) ?? new Set([first]);
}

function setsIntersect(a: Set<string>, b: Set<string>): boolean {
  for (const v of a) if (b.has(v)) return true;
  return false;
}

type SurnameMatchLevel = 'exact' | 'partial' | 'none';

/**
 * Full normalized equality = exact. A shared component of a compound/
 * hyphenated surname (Garcia-Lopez vs Garcia) = partial — that is NEVER
 * treated as green; it needs human attention. No overlap = none.
 */
function surnameMatchLevel(a: ParsedName, b: ParsedName): SurnameMatchLevel {
  if (a.last && a.last === b.last) return 'exact';
  if (a.lastParts.length === 0 || b.lastParts.length === 0) return 'none';
  const bSet = new Set(b.lastParts);
  for (const part of a.lastParts) {
    if (bSet.has(part)) return 'partial';
  }
  return 'none';
}

/**
 * Compare a source name against an entered name.
 * sourceRaw === null/undefined/empty means the source didn't provide it.
 */
export function compareNames(
  sourceRaw: string | null | undefined,
  enteredRaw: string | null | undefined
): NameCompareResult {
  const sourceEmpty = !sourceRaw || !sourceRaw.trim();
  const enteredEmpty = !enteredRaw || !enteredRaw.trim();

  if (sourceEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'Source e-prescription did not provide a patient/prescriber name to compare.'
    };
  }
  if (enteredEmpty) {
    return {
      status: 'yellow',
      reasonCode: 'not_provided',
      explanation: 'No name was entered in PioneerRx to compare against the source.'
    };
  }

  const a = parseName(sourceRaw);
  const b = parseName(enteredRaw);

  const surnameLevel = surnameMatchLevel(a, b);
  if (surnameLevel === 'none') {
    return {
      status: 'red',
      reasonCode: 'surname_mismatch',
      explanation: `Surname "${a.last}" on the source does not match entered surname "${b.last}".`
    };
  }

  // Classify the first-name relationship.
  type FirstLevel = 'exact' | 'nickname' | 'fuzzy' | 'mismatch';
  let firstLevel: FirstLevel;
  if (a.first === b.first) {
    firstLevel = 'exact';
  } else if (setsIntersect(canonicalsOf(a.first), canonicalsOf(b.first))) {
    firstLevel = 'nickname';
  } else if (
    a.first.length >= 3 &&
    b.first.length >= 3 &&
    (a.first.startsWith(b.first) || b.first.startsWith(a.first))
  ) {
    firstLevel = 'fuzzy';
  } else {
    firstLevel = 'mismatch';
  }

  if (firstLevel === 'mismatch') {
    return {
      status: 'red',
      reasonCode: 'first_name_mismatch',
      explanation: `First name "${a.first}" on the source does not match entered first name "${b.first}", and surnames matched.`
    };
  }

  // Generational suffix contradiction (Jr vs Sr with the same name is a
  // different person — often father/son at the same address).
  if (a.suffix && b.suffix && a.suffix !== b.suffix) {
    return {
      status: 'red',
      reasonCode: 'suffix_mismatch',
      explanation: `Generational suffix differs ("${a.suffix}" vs "${b.suffix}") — Jr/Sr/II with the same name usually means a different person.`
    };
  }

  // Partial compound-surname overlap is never green — a shared component
  // (Garcia-Lopez vs Garcia) needs a human glance.
  if (surnameLevel === 'partial') {
    return {
      status: 'yellow',
      reasonCode: 'surname_partial',
      explanation: `Surname "${a.last}" and "${b.last}" share a component but are not identical (compound/hyphenated surname partially matches); needs human review.`
    };
  }

  // Suffix present on only one side: kept YELLOW (not green-with-note)
  // deliberately — a dropped Jr/Sr can mask a father/son swap at the
  // same address with the same prescriber, so it warrants attention.
  if ((a.suffix === null) !== (b.suffix === null)) {
    return {
      status: 'yellow',
      reasonCode: 'suffix_dropped',
      explanation: `Name matches but the generational suffix ("${a.suffix ?? b.suffix}") appears on only one side — confirm this is not a Jr/Sr mixup.`
    };
  }

  if (firstLevel === 'nickname') {
    return {
      status: 'yellow',
      reasonCode: 'nickname_match',
      explanation: `First name "${a.first}" and "${b.first}" are recognized as common nickname variants of the same name.`
    };
  }

  if (firstLevel === 'fuzzy') {
    return {
      status: 'yellow',
      reasonCode: 'nickname_match',
      explanation: `First name "${a.first}" and "${b.first}" appear to be a truncated/shortened variant of the same name (lower-confidence prefix match, not a known nickname pair); verify.`
    };
  }

  return {
    status: 'green',
    reasonCode: 'exact_match',
    explanation: 'Name matches exactly after case/punctuation/order normalization.'
  };
}
