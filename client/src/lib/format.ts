/** Presentation helpers. Nothing here touches the network or the API's shapes. */

const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

import { regional } from './regional.svelte';
import { dateOptions, exactDate } from './regional';

/**
 * "3m", "yesterday", "12 Mar" — a column of these is read for its shape, so the recent past is
 * counted and anything older is dated. Empty for a missing timestamp rather than "Invalid Date".
 */
export function relativeTime(value: string | null | undefined): string {
  if (!value) return '';

  const then = new Date(value);
  if (Number.isNaN(then.getTime())) return '';

  const seconds = Math.round((then.getTime() - Date.now()) / 1000);
  const abs = Math.abs(seconds);

  if (abs < 45) return 'just now';
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), 'hour');
  if (abs < 6 * 86400) return rtf.format(Math.round(seconds / 86400), 'day');

  return formatDate(value);
}

/** A date, with the year only when it is not this one. */
export function formatDate(value: string | null | undefined): string {
  if (!value) return '';

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const options = dateOptions(value, regional.timeZone);
  const year = new Intl.DateTimeFormat('en', { year: 'numeric', ...options });
  const currentYear = new Intl.DateTimeFormat('en', {
    year: 'numeric', timeZone: regional.timeZone
  }).format(new Date());
  return new Intl.DateTimeFormat(regional.locale, {
    day: 'numeric', month: 'short',
    ...(year.format(date) !== currentYear ? { year: 'numeric' as const } : {}),
    ...options
  }).format(date);
}

/** The full moment, for the title attribute behind a relative time. */
export function formatExact(value: string | null | undefined): string {
  if (!value) return '';

  return exactDate(value, regional.locale, regional.timeZone);
}

/**
 * Parses either shape the API sends: a timestamp with an offset, or a bare `YYYY-MM-DD`.
 *
 * The bare form has to be read as local midnight. `new Date('2026-09-30')` is parsed as UTC, which
 * west of Greenwich displays as the 29th — a due date off by one for half the world.
 */
export function parseDate(value: string | null | undefined): Date | null {
  if (!value) return null;

  const dateOnly = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);

  const date = dateOnly
    ? new Date(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3]))
    : new Date(value);

  return Number.isNaN(date.getTime()) ? null : date;
}

/** `YYYY-MM-DD` for a <input type="date">, which will not accept anything else. */
export function toDateInput(value: string | null | undefined): string {
  const date = parseDate(value);
  if (!date) return '';

  return [
    date.getFullYear(),
    String(date.getMonth() + 1).padStart(2, '0'),
    String(date.getDate()).padStart(2, '0')
  ].join('-');
}

/** True for a due date that has already passed. Today is not overdue. */
export function isOverdue(value: string | null | undefined): boolean {
  const date = parseDate(value);
  if (!date) return false;

  const today = new Date();
  today.setHours(0, 0, 0, 0);

  return date < today;
}

/** Up to two initials, from a display name. "Dana Whitfield" → "DW", "dana" → "D". */
export function initials(name: string | null | undefined): string {
  const words = (name ?? '').trim().split(/\s+/).filter(Boolean);

  if (words.length === 0) return '?';
  if (words.length === 1) return words[0].slice(0, 1).toUpperCase();

  return (words[0][0] + words[words.length - 1][0]).toUpperCase();
}

/**
 * A stable colour for a monogram, so the same person is the same colour everywhere without the server
 * storing one. Hue only: saturation and lightness are fixed so every avatar carries the same weight.
 */
export function avatarHue(seed: string): number {
  let hash = 0;

  for (let i = 0; i < seed.length; i++) {
    hash = (hash << 5) - hash + seed.charCodeAt(i);
    hash |= 0;
  }

  return Math.abs(hash) % 360;
}

/**
 * Black or white, whichever is readable on the given background.
 *
 * Team and label colours are arbitrary hex the server accepts as typed, so the text drawn on them
 * cannot be a fixed colour. This is the WCAG relative-luminance formula, which handles the pale half
 * of a palette that a naive brightness average gets wrong.
 */
export function readableOn(hex: string): string {
  const rgb = parseHex(hex);
  if (!rgb) return '#ffffff';

  const channel = (value: number) => {
    const c = value / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };

  const luminance =
    0.2126 * channel(rgb[0]) + 0.7152 * channel(rgb[1]) + 0.0722 * channel(rgb[2]);

  return luminance > 0.45 ? '#16181c' : '#ffffff';
}

/** The same colour at an opacity, for the tinted background behind a label chip. */
export function alpha(hex: string, amount: number): string {
  const rgb = parseHex(hex);
  return rgb ? `rgb(${rgb[0]} ${rgb[1]} ${rgb[2]} / ${Math.round(amount * 100)}%)` : 'transparent';
}

function parseHex(hex: string): [number, number, number] | null {
  const match = /^#?([\da-f]{3}|[\da-f]{6})$/i.exec((hex ?? '').trim());
  if (!match) return null;

  const value =
    match[1].length === 3
      ? match[1]
          .split('')
          .map((c) => c + c)
          .join('')
      : match[1];

  return [
    parseInt(value.slice(0, 2), 16),
    parseInt(value.slice(2, 4), 16),
    parseInt(value.slice(4, 6), 16)
  ];
}

/** "1 issue" / "2 issues", without a template literal at every call site. */
export function plural(count: number, singular: string, plural?: string): string {
  return `${count} ${count === 1 ? singular : (plural ?? `${singular}s`)}`;
}

/** Bytes as a human size. Attachments report theirs, and "4194304" is not a size anyone reads. */
export function fileSize(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) return '';
  if (bytes < 1024) return `${bytes} B`;

  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }

  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

/** The calendar day a moment falls on, in the user's time zone, as `YYYY-MM-DD` — a grouping key. */
export function dayKey(value: string | Date): string {
  return new Intl.DateTimeFormat('en-CA', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    timeZone: regional.timeZone
  }).format(typeof value === 'string' ? new Date(value) : value);
}

/** A feed's day heading: "Today", "Yesterday", then the date. */
export function dayLabel(value: string): string {
  const key = dayKey(value);
  const now = new Date();

  if (key === dayKey(now)) return 'Today';
  if (key === dayKey(new Date(now.getTime() - 86_400_000))) return 'Yesterday';

  return formatDate(value);
}

/** Consecutive items grouped by the day they happened, for a list already in time order. */
export function byDay<T>(items: T[], at: (item: T) => string): { key: string; label: string; items: T[] }[] {
  const groups: { key: string; label: string; items: T[] }[] = [];

  for (const item of items) {
    const key = dayKey(at(item));
    const last = groups.at(-1);

    if (last?.key === key) last.items.push(item);
    else groups.push({ key, label: dayLabel(at(item)), items: [item] });
  }

  return groups;
}
