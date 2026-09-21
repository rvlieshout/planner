import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

/*
 * What this build is, read once for both configs: vite.config.ts bakes it into the bundle for the
 * status bar, and svelte.config.js publishes it as `_app/version.json`, which a running client polls
 * to learn that a newer build has been deployed. One source, so the two can never disagree.
 */

export const { version } = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8'));

// The commit this bundle was built from. CI passes it in, because the image build has no .git to
// ask; a local build falls back to the checkout's HEAD, and anything else says so rather than
// claiming a commit it cannot name.
function readSha() {
  const fromEnv = process.env.PLANNER_BUILD_SHA?.trim();
  if (fromEnv) return fromEnv;

  try {
    return execFileSync('git', ['rev-parse', 'HEAD'], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore']
    }).trim();
  } catch {
    return 'unknown';
  }
}

export const sha = readSha();

/*
 * The name a deployment is known by: the version, then the commit. The commit is part of it so a
 * redeploy that forgot to bump package.json is still a new build to the clients already running.
 * `src/lib/update.svelte.ts` splits it again at the '+'.
 */
export const buildName = `${version}+${sha}`;
