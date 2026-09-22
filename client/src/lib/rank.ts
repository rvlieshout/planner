/*
 * Lexicographic rank keys — a line-for-line port of `Planner.Domain.Common.Rank` on the server.
 *
 * Every hand-ordered sequence (a board column, a team's projects and workflow states, a project's
 * milestones) is ordered by a string that sorts byte-wise into place. Between any two keys there is
 * always another, so a move writes one row and never runs out of room the way a `double` midpoint does.
 *
 * The port exists so that the key a drop shows optimistically is the one the server is about to write:
 * the realtime echo then confirms the order on screen rather than rearranging it. Keys compare with
 * `<`, never `localeCompare` — the order is ordinal, which is what Postgres's `COLLATE "C"` gives too.
 */

const DIGITS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';
const ZERO = '0';
const NINE = 'z';
const SMALLEST = 'A' + ZERO.repeat(26);

/** The key of the first row of an empty sequence. */
export const FIRST_RANK = 'a0';

/** Ordinal comparison, for `Array.prototype.sort`. */
export function compareRank(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * A key strictly between `after` and `before`; either may be null for an open end.
 * Throws if a key is malformed or `after` does not sort strictly before `before`.
 */
export function rankBetween(after: string | null | undefined, before: string | null | undefined): string {
  after ??= null;
  before ??= null;

  if (after !== null) validate(after);
  if (before !== null) validate(before);
  if (after !== null && before !== null && after >= before) {
    throw new Error(`Rank '${after}' does not sort before '${before}'.`);
  }

  if (after === null) {
    if (before === null) return FIRST_RANK;

    const integer = integerPart(before);
    const fraction = before.slice(integer.length);

    if (integer === SMALLEST) return integer + midpoint('', fraction);
    if (integer < before) return integer;

    const previous = decrement(integer);
    if (previous === null) throw new Error('There is no rank before that one.');
    return previous;
  }

  if (before === null) {
    const integer = integerPart(after);
    return increment(integer) ?? integer + midpoint(after.slice(integer.length), null);
  }

  const afterInteger = integerPart(after);
  const beforeInteger = integerPart(before);

  if (afterInteger === beforeInteger) {
    return afterInteger + midpoint(after.slice(afterInteger.length), before.slice(beforeInteger.length));
  }

  const next = increment(afterInteger);
  if (next === null) throw new Error('There is no rank after that one.');
  return next < before ? next : afterInteger + midpoint(after.slice(afterInteger.length), null);
}

/**
 * The key for a row placed at `index` among `ranks` — the other rows of its list, ascending.
 *
 * It follows the row above and takes a key before the next *greater* one, so rows that share a rank
 * (two concurrent moves into the same gap can make them) never leave it without a key to take.
 */
export function rankAt(ranks: string[], index: number): string {
  const after = ranks[index - 1];
  if (after === undefined) return rankBetween(null, ranks[0]);

  return rankBetween(after, ranks.slice(index).find((rank) => rank > after));
}

function validate(key: string): void {
  const length = integerLength(key[0]);
  const wellFormed =
    [...key].every((c) => DIGITS.includes(c)) &&
    length > 0 &&
    length <= key.length &&
    key !== SMALLEST &&
    (length === key.length || key[key.length - 1] !== ZERO);

  if (!wellFormed) throw new Error(`'${key}' is not a rank key.`);
}

function midpoint(after: string, before: string | null): string {
  if (before !== null) {
    let shared = 0;
    while ((after[shared] ?? ZERO) === before[shared]) shared++;

    if (shared > 0) return before.slice(0, shared) + midpoint(after.slice(shared), before.slice(shared));
  }

  const low = after ? DIGITS.indexOf(after[0]) : 0;
  const high = before !== null ? DIGITS.indexOf(before[0]) : DIGITS.length;

  if (high - low > 1) return DIGITS[Math.round(0.5 * (low + high))];
  if (before !== null && before.length > 1) return before.slice(0, 1);

  return DIGITS[low] + midpoint(after.slice(1), null);
}

function integerLength(head: string | undefined): number {
  if (head === undefined) return 0;
  if (head >= 'a' && head <= 'z') return head.charCodeAt(0) - 'a'.charCodeAt(0) + 2;
  if (head >= 'A' && head <= 'Z') return 'Z'.charCodeAt(0) - head.charCodeAt(0) + 2;
  return 0;
}

function integerPart(key: string): string {
  return key.slice(0, integerLength(key[0]));
}

function increment(integer: string): string | null {
  const head = integer[0];
  const digits = [...integer.slice(1)];

  for (let i = digits.length - 1; i >= 0; i--) {
    const next = DIGITS.indexOf(digits[i]) + 1;
    if (next < DIGITS.length) {
      digits[i] = DIGITS[next];
      return head + digits.join('');
    }
    digits[i] = ZERO;
  }

  if (head === 'Z') return 'a' + ZERO;
  if (head === 'z') return null;

  const nextHead = String.fromCharCode(head.charCodeAt(0) + 1);
  if (nextHead > 'a') digits.push(ZERO);
  else digits.pop();

  return nextHead + digits.join('');
}

function decrement(integer: string): string | null {
  const head = integer[0];
  const digits = [...integer.slice(1)];

  for (let i = digits.length - 1; i >= 0; i--) {
    const next = DIGITS.indexOf(digits[i]) - 1;
    if (next >= 0) {
      digits[i] = DIGITS[next];
      return head + digits.join('');
    }
    digits[i] = NINE;
  }

  if (head === 'a') return 'Z' + NINE;
  if (head === 'A') return null;

  const nextHead = String.fromCharCode(head.charCodeAt(0) - 1);
  if (nextHead < 'Z') digits.push(NINE);
  else digits.pop();

  return nextHead + digits.join('');
}
