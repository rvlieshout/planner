/*
 * The version in the status bar.
 *
 * Injected at build time from package.json by vite.config.ts, so there is one number to bump and the
 * bundle, the status bar and the release tag cannot disagree.
 */
declare const __APP_VERSION__: string;

export const VERSION = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : 'dev';
