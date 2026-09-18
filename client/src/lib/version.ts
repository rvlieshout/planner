/*
 * The version in the status bar.
 *
 * Injected at build time from package.json by vite.config.ts, so there is one number to bump and the
 * bundle, the status bar and the release tag cannot disagree. The commit comes the same way, so a
 * bug report can name the exact build it was seen on and not only its version.
 */
declare const __APP_VERSION__: string;
declare const __BUILD_SHA__: string;

export const VERSION = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : 'dev';

/* The commit this bundle was built from, or 'unknown' when the build had none to name. */
export const BUILD_SHA = typeof __BUILD_SHA__ === 'string' && __BUILD_SHA__ ? __BUILD_SHA__ : 'unknown';

/* What the status bar's version reveals on hover: the full commit, ready to paste into a checkout. */
export const BUILD_LABEL = `Planner ${VERSION} — build ${BUILD_SHA}`;
