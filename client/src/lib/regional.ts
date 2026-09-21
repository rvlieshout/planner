/** The small, fixed set offered by profile and user-management forms. */
export const TIME_ZONES = [
  { value: 'Europe/Amsterdam', label: 'Amsterdam' },
  { value: 'Europe/London', label: 'London' },
  { value: 'America/New_York', label: 'New York' },
  { value: 'America/Los_Angeles', label: 'Los Angeles' },
  { value: 'UTC', label: 'UTC' }
] as const;

export function defaultTimeZone(): string {
  const detected = Intl.DateTimeFormat().resolvedOptions().timeZone;
  return TIME_ZONES.some((zone) => zone.value === detected) ? detected : 'UTC';
}

/** Timezones are only a hint: users can override the regional format independently. */
export function resolveLocale(choice: string, timeZone: string, browserLocale = 'en-GB'): string {
  if (choice !== 'auto') return choice;
  return timeZone === 'Europe/Amsterdam' ? 'nl-NL' : browserLocale;
}

export function resolveTimeZone(timeZone?: string | null): string {
  if (timeZone) {
    try {
      new Intl.DateTimeFormat('en', { timeZone });
      return timeZone;
    } catch { /* Older profiles may contain an unsupported timezone. */ }
  }
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
}

/** Calendar dates are not instants and must never shift with the user's timezone. */
export function dateOptions(value: string, timeZone: string): Intl.DateTimeFormatOptions {
  return { timeZone: /^\d{4}-\d{2}-\d{2}$/.test(value) ? 'UTC' : timeZone };
}

export function calendarDate(value: string, locale: string): string {
  if (!value) return '';
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isNaN(date.getTime()) ? '' : new Intl.DateTimeFormat(locale, {
    year: 'numeric', month: '2-digit', day: '2-digit', timeZone: 'UTC'
  }).format(date);
}

export function exactDate(value: string, locale: string, timeZone: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : new Intl.DateTimeFormat(locale, {
    dateStyle: 'medium', timeStyle: 'short', ...dateOptions(value, timeZone)
  }).format(date);
}
