import assert from 'node:assert/strict';
import { createServer } from 'vite';

// Run the actual Svelte token module through Vite, with browser storage and HTTP controlled here.
const values = new Map([['planner.refreshToken', 'previous-browser-session']]);
globalThis.localStorage = {
  getItem: key => values.get(key) ?? null,
  setItem: (key, value) => values.set(key, value),
  removeItem: key => values.delete(key)
};
const originalFetch = globalThis.fetch;
const server = await createServer({ server: { middlewareMode: true }, appType: 'custom' });
const ok = () => Response.json({ access_token: 'access', refresh_token: 'renewed', expires_in: 3600 });
try {
  const { tokens } = await server.ssrLoadModule('/src/lib/auth/tokens.svelte.ts');
  const { request, setUnauthorizedHandler } = await server.ssrLoadModule('/src/lib/api/http.ts');
  assert.equal(tokens.canRestore, true);
  globalThis.fetch = async (_url, init) => {
    assert.equal(init.body.get('grant_type'), 'refresh_token');
    assert.equal(init.body.get('refresh_token'), 'previous-browser-session');
    return ok();
  };
  await tokens.refresh();
  assert.equal(values.get('planner.refreshToken'), 'renewed');
  console.log('PASS: Browser restart restores and persists a renewed session');

  globalThis.fetch = async (_url, init) => {
    assert.equal(init.body.get('grant_type'), 'urn:planner:params:oauth:grant-type:passkey');
    assert.equal(init.body.get('credential'), 'signed-credential');
    assert.ok(init.body.get('scope').includes('offline_access'));
    return ok();
  };
  await tokens.signInWithPasskey('signed-credential');
  assert.equal(tokens.canRestore, true);
  console.log('PASS: Passkey sign-in requests and stores a remembered session');

  for (const status of [0, 429, 503]) {
    const fail = () => {
      if (status === 0) throw new TypeError('Network unavailable');
      return Response.json({ error: 'temporarily_unavailable' }, { status });
    };
    globalThis.fetch = async () => fail();
    await assert.rejects(tokens.refresh());
    assert.equal(tokens.canRestore, true);
    assert.equal(values.get('planner.refreshToken'), 'renewed');
    let signedOut = false;
    setUnauthorizedHandler(() => { signedOut = true; tokens.clear(); });
    let calls = 0;
    globalThis.fetch = async () => ++calls === 1 ? new Response(null, { status: 401 }) : fail();
    await assert.rejects(request('/api/v1/me'));
    assert.equal(signedOut, false);
    assert.equal(tokens.canRestore, true);
    console.log(`PASS: Refresh failure ${status} preserves the session, including after an API 401`);
  }
  globalThis.fetch = async () => Response.json({ error: 'invalid_grant' }, { status: 400 });
  await assert.rejects(tokens.refresh());
  assert.equal(tokens.canRestore, false);
  assert.equal(values.has('planner.refreshToken'), false);
  console.log('PASS: Revoked or expired refresh credentials are cleared');

  globalThis.fetch = async () => ok();
  await tokens.signInWithPasskey('signed-credential');
  tokens.clear();
  assert.equal(tokens.accessToken, null);
  assert.equal(values.has('planner.refreshToken'), false);
  console.log('PASS: Explicit sign-out clears the remembered session');
} finally {
  globalThis.fetch = originalFetch;
  delete globalThis.localStorage;
  await server.close();
}
