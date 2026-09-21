import { session } from '$lib/auth/session.svelte';
import { settings } from '$lib/settings.svelte';
import { resolveLocale, resolveTimeZone } from './regional';

export const regional = {
  get timeZone() {
    return resolveTimeZone(session.user?.timeZone);
  },
  get locale() {
    return resolveLocale(settings.dateLocale, this.timeZone,
      typeof navigator === 'undefined' ? 'en-GB' : navigator.language);
  }
};
