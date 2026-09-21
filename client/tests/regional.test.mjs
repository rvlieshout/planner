import { test } from 'node:test';
import assert from 'node:assert/strict';
import { calendarDate, exactDate, resolveLocale, resolveTimeZone } from '../src/lib/regional.ts';

test('Amsterdam chooses Dutch formats even with an English browser', () => {
  const locale = resolveLocale('auto', 'Europe/Amsterdam', 'en-US');
  assert.equal(locale, 'nl-NL');
  assert.equal(calendarDate('2026-09-18', locale), '18-09-2026');
  assert.match(exactDate('2026-09-18T12:30:00Z', locale, 'Europe/Amsterdam'), /14:30/);
});

test('explicit culture takes precedence; unknown timezone regions keep browser culture', () => {
  assert.equal(resolveLocale('nl-NL', 'America/New_York', 'en-US'), 'nl-NL');
  assert.equal(resolveLocale('en-US', 'Europe/Amsterdam', 'en-GB'), 'en-US');
  assert.equal(resolveLocale('auto', 'America/Toronto', 'fr-CA'), 'fr-CA');
});

test('timestamps respect daylight saving and day rollover', () => {
  assert.match(exactDate('2026-01-18T12:30:00Z', 'nl-NL', 'Europe/Amsterdam'), /13:30/);
  assert.equal(exactDate('2026-09-18T23:30:00Z', 'nl-NL', 'Europe/Amsterdam'),
    exactDate('2026-09-19T01:30:00+02:00', 'nl-NL', 'Europe/Amsterdam'));
});

test('calendar dates remain unchanged across host timezones', () => {
  const previous = process.env.TZ;
  try {
    for (const zone of ['America/Los_Angeles', 'Pacific/Auckland']) {
      process.env.TZ = zone;
      assert.equal(calendarDate('2026-09-18', 'nl-NL'), '18-09-2026');
    }
  } finally {
    if (previous === undefined) delete process.env.TZ;
    else process.env.TZ = previous;
  }
});

test('empty values and unsupported saved timezones do not crash rendering', () => {
  assert.equal(calendarDate('', 'nl-NL'), '');
  assert.equal(exactDate('invalid', 'nl-NL', 'Europe/Amsterdam'), '');
  assert.doesNotThrow(() => resolveTimeZone('invalid/timezone'));
});
