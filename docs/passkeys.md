# Passkey sign-in

Planner offers **Sign in with a passkey** first. It uses the browser's WebAuthn support and
ASP.NET Core Identity's verifier; it does not use an external identity provider or outgoing email.

## First-time setup and recovery

1. An administrator creates the account using the existing Users screen and shares its initial
   password directly through an appropriate channel.
2. The user opens **First-time setup or recovery**, signs in once with that password, and is taken
   to **Preferences**, where they name and add a passkey.
3. Future sign-ins use the passkey, without entering an email address or Planner password.
4. Add a second passkey on another device or hardware key as a backup. Preferences lists registered
   passkeys and lets their owner remove unused or lost credentials.

Existing accounts and passwords remain valid. If all passkeys are unavailable, the user can use
that password or ask an administrator to reset it in Users and share the replacement directly.
After recovery, add a replacement passkey and remove any lost credentials. Keep an offline copy of
the owner's recovery password and preferably two owner passkeys: the initial seed password does
not reset an existing owner's account. No recovery email or magic link is sent.

Removing a passkey prevents new sign-ins with it; it does not end existing sessions. Deactivate a
compromised account to prevent further token issuance, including refreshes. Already-issued access
tokens remain valid until expiration, as in the existing token model.

## Remembered sessions

The browser continues to store its refresh token and restores the session after reload or restart.
Access tokens remain in memory. The default refresh lifetime is now **30 days**, renewed when the
session refreshes. Override with `Planner__Auth__RefreshTokenDays`. Signing out clears this browser's
stored credentials. Browser storage clearing, private browsing, expiration, or account deactivation
can require signing in again. Rate limiting and temporary server/network failures do not erase the
stored refresh token. Users should sign out on shared devices.

## Hosting and deployment

- Apply the `UserPasskeys` EF migration (normally automatic at API startup). It adds `user_passkeys`
  with Identity's public credential data; private keys and biometrics never reach Planner.
- Use HTTPS on a stable public hostname. Browsers also allow localhost for development. Plain HTTP
  over a LAN hostname or IP cannot use passkeys; the UI explains the fallback.
- The proxy must preserve the public Host and forward the original HTTPS scheme. Configure
  `Planner__Auth__TrustedProxyHops` correctly for the deployment. The Vite API/auth proxies preserve
  Host for localhost development. Origin validation matches the exact public scheme, host, and port.
  Caddy must also trust the upstream proxy to preserve HTTPS; see [production 400 troubleshooting](deploy-coolify.md#passkey-setup-returns-400-behind-coolify).
- Changing the hostname requires enrolling passkeys for the new hostname using the recovery path.
- Pending ceremonies are held in a bounded in-process cache for five minutes and consumed atomically.
  An HttpOnly, SameSite Strict cookie binds each ceremony to its browser; registration state is also
  bound to the authenticated account. Restarting the API cancels pending prompts, so retry them.
  Multi-replica hosting requires sticky routing for each ceremony or a shared store with atomic
  consumption before scaling out. Registered credentials remain in Postgres.
- Keep the existing key volume mounted so remembered sessions survive API/container restarts.

## Verification

Run `dotnet run --project tests/Planner.Auth.Checks` for the native WebAuthn verifier checks, including
real P-256 registration/assertion, incorrect challenge/origin/signature/user handle, missing user
verification, removed credentials, browser binding, concurrent replay, and Postgres model generation.
Run `npm run check:auth`, `npm run check`, and `npm run build` from `client` for session regression checks and browser code validation.

Before deployment, exercise Windows Hello, a phone passkey, or a hardware key on the final HTTPS
hostname: register, sign out, sign in with the passkey, restart the browser, add/remove a backup, and
try administrator-assisted recovery. Automated verifier checks do not test physical authenticators.
