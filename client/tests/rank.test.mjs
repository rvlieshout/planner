import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FIRST_RANK, compareRank, rankAt, rankBetween } from '../src/lib/rank.ts';

// The same vectors tests/Planner.Api.Checks/RankChecks.cs holds the server to, taken from Rocicorp's
// fractional-indexing suite. A null expectation means the pair must be refused.
const vectors = [
  [null, null, 'a0'],
  [null, 'a0', 'Zz'],
  [null, 'Zz', 'Zy'],
  ['a0', null, 'a1'],
  ['a1', null, 'a2'],
  ['a0', 'a1', 'a0V'],
  ['a1', 'a2', 'a1V'],
  ['a0V', 'a1', 'a0l'],
  ['Zz', 'a0', 'ZzV'],
  ['Zz', 'a1', 'a0'],
  [null, 'Y00', 'Xzzz'],
  ['bzz', null, 'c000'],
  ['a0', 'a0V', 'a0G'],
  ['a0', 'a0G', 'a08'],
  ['b125', 'b129', 'b127'],
  ['a0', 'a1V', 'a1'],
  ['Zz', 'a01', 'a0'],
  [null, 'a0V', 'a0'],
  [null, 'b999', 'b99'],
  [null, 'A000000000000000000000000001', 'A000000000000000000000000000V'],
  ['zzzzzzzzzzzzzzzzzzzzzzzzzzy', null, 'zzzzzzzzzzzzzzzzzzzzzzzzzzz'],
  ['zzzzzzzzzzzzzzzzzzzzzzzzzzz', null, 'zzzzzzzzzzzzzzzzzzzzzzzzzzzV'],
  [null, 'A00000000000000000000000000', null],
  ['a00', null, null],
  ['a00', 'a1', null],
  ['0', '1', null],
  ['a1', 'a0', null],
  ['a1', 'a1', null]
];

test('keys match the reference vectors the server is held to', () => {
  for (const [after, before, expected] of vectors) {
    if (expected === null) {
      assert.throws(() => rankBetween(after, before), undefined, `between(${after}, ${before}) is refused`);
    } else {
      assert.equal(rankBetween(after, before), expected, `between(${after}, ${before})`);
    }
  }
});

test('the first key of an empty list is a0, and undefined reads as an open end', () => {
  assert.equal(FIRST_RANK, 'a0');
  assert.equal(rankBetween(undefined, undefined), 'a0');
});

test('keys the migration gave existing rows can be placed around', () => {
  assert.equal(rankBetween('d0001', 'd0002'), 'd0001V');
  assert.equal(rankBetween('d00zz', null), 'd0100');
  assert.equal(rankBetween(null, 'd0001'), 'd0000');
});

test('comparison is ordinal, not linguistic', () => {
  assert.deepEqual(['a0a', 'a1', 'a0V', 'a0v', 'a0Z'].sort(compareRank), ['a0V', 'a0Z', 'a0a', 'a0v', 'a1']);
});

test('rankAt places a row at an index, and steps past tied ranks', () => {
  assert.equal(rankAt([], 0), 'a0');
  assert.equal(rankAt(['a0', 'a1'], 0), 'Zz');
  assert.equal(rankAt(['a0', 'a1'], 1), 'a0V');
  assert.equal(rankAt(['a0', 'a1'], 2), 'a2');
  // Two rows share a0: there is no key between them, so the row goes after both.
  assert.equal(rankAt(['a0', 'a0', 'a1'], 1), 'a0V');
});

test('random moves keep the list in exactly the order the moves produced', () => {
  let seed = 20260922;
  const random = (n) => {
    seed = (seed * 1103515245 + 12345) % 2147483648;
    return seed % n;
  };

  const list = [];
  for (let id = 0; id < 5000; id++) {
    const moving = list.length > 0 && random(3) > 0 ? random(list.length) : -1;
    const row = moving >= 0 ? list.splice(moving, 1)[0].id : id;
    const at = random(list.length + 1);
    list.splice(at, 0, { id: row, rank: rankAt(list.map((entry) => entry.rank), at) });
  }

  const sorted = [...list].sort((a, b) => compareRank(a.rank, b.rank));
  assert.deepEqual(sorted.map((entry) => entry.id), list.map((entry) => entry.id));
  assert.ok(Math.max(...list.map((entry) => entry.rank.length)) <= 12);
});
