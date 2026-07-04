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

const NICKNAME_TO_CANONICAL: Map<string, string> = new Map();
for (const group of NICKNAME_GROUPS) {
  const canonical = group[0];
  if (!canonical) continue;
  for (const variant of group) {
    // First mapping wins so overlapping names (e.g. "pat", "al") keep a
    // stable canonical bucket rather than being silently overwritten.
    if (!NICKNAME_TO_CANONICAL.has(variant)) {
      NICKNAME_TO_CANONICAL.set(variant, canonical);
    }
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
 * Parse a free-text name into { first, last } handling both
 * "Last, First" and "First Last" orderings. Hyphenated surnames are kept
 * intact as a single token for surname comparison but also exploded into
 * parts for tolerant matching (Garcia-Lopez vs Garcia Lopez vs Garcia).
 */
export interface ParsedName {
  first: string;
  last: string;
  /** Hyphenation-tolerant surname parts, e.g. ["garcia", "lopez"]. */
  lastParts: string[];
}

export function parseName(raw: string): ParsedName {
  const folded = foldCase(raw);
  let first = '';
  let last = '';

  if (folded.includes(',')) {
    const [lastPart, firstPart] = folded.split(',').map((p) => p.trim());
    last = lastPart ?? '';
    first = (firstPart ?? '').split(' ')[0] ?? '';
  } else {
    const parts = folded.split(' ').filter(Boolean);
    if (parts.length >= 2) {
      first = parts[0] ?? '';
      last = parts[parts.length - 1] ?? '';
    } else {
      first = parts[0] ?? '';
      last = '';
    }
  }

  const lastParts = last
    .split(/[-\s]+/)
    .map((p) => p.trim())
    .filter(Boolean);

  return { first, last: last.replace(/-/g, ' '), lastParts };
}

function canonicalFirst(first: string): string {
  return NICKNAME_TO_CANONICAL.get(first) ?? first;
}

function surnamesMatch(a: ParsedName, b: ParsedName): boolean {
  if (a.last === b.last) return true;
  // Hyphenation tolerance: one side may have only one component of a
  // hyphenated/compound surname (e.g. "Garcia" vs "Garcia-Lopez" is a
  // real mismatch for our purposes ONLY if neither part matches at all;
  // if either full part matches, treat as tolerant-equal).
  if (a.lastParts.length === 0 || b.lastParts.length === 0) return false;
  const aSet = new Set(a.lastParts);
  const bSet = new Set(b.lastParts);
  for (const part of aSet) {
    if (bSet.has(part)) return true;
  }
  return false;
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

  const surnameOk = surnamesMatch(a, b);
  if (!surnameOk) {
    return {
      status: 'red',
      reasonCode: 'surname_mismatch',
      explanation: `Surname "${a.last}" on the source does not match entered surname "${b.last}".`
    };
  }

  if (a.first === b.first) {
    return {
      status: 'green',
      reasonCode: 'exact_match',
      explanation: 'Name matches exactly after case/punctuation/order normalization.'
    };
  }

  const canonA = canonicalFirst(a.first);
  const canonB = canonicalFirst(b.first);
  if (canonA && canonA === canonB) {
    return {
      status: 'yellow',
      reasonCode: 'nickname_match',
      explanation: `First name "${a.first}" and "${b.first}" are recognized as common nickname variants of the same name.`
    };
  }

  // Simple fuzzy tolerance: one is a prefix of the other (truncated entry).
  if (a.first.length >= 3 && b.first.length >= 3 && (a.first.startsWith(b.first) || b.first.startsWith(a.first))) {
    return {
      status: 'yellow',
      reasonCode: 'nickname_match',
      explanation: `First name "${a.first}" and "${b.first}" appear to be a truncated/shortened variant of the same name.`
    };
  }

  return {
    status: 'red',
    reasonCode: 'first_name_mismatch',
    explanation: `First name "${a.first}" on the source does not match entered first name "${b.first}", and surnames matched.`
  };
}
