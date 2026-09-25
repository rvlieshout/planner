# MCP authorization

AI assistants that talk to Planner over MCP sign in with the OAuth 2.1 authorization code flow and
PKCE. They never see the user's password or passkey: the user signs in on this installation's own web
client and approves the assistant there, and the assistant receives a code only it can redeem.

This covers how those tokens are issued. The `/mcp` endpoint itself, and the protected-resource
metadata that points clients here, are separate work.

## The flow

```
MCP client ──► GET /connect/authorize?client_id=planner-mcp&redirect_uri&code_challenge&resource…
                 │  OpenIddict checks the client, redirect_uri, PKCE (S256 only), scopes and resource
                 │  no decision cookie → 302 /app/authorize?return=/connect/authorize?…
                 ▼
/app/authorize     signs in first if needed (the normal login page, which comes back afterwards),
                   shows which client is asking and where it will be sent back to
                 │  Allow/Deny → POST /connect/authorize/consent   (the web client's bearer token)
                 │               ◄── planner.authorize cookie: who, which client, what decision; 5 min
                 │  location = return
                 ▼
GET /connect/authorize  (cookie present, single use) → code, or access_denied → redirect_uri
MCP client ──► POST /connect/token  grant_type=authorization_code + code_verifier → tokens
```

## Why it is built this way

- **No sign-in page in the API.** The web client already does passkeys and passwords, and is served
  from the same origin. Rather than a second login screen, the API borrows the web client's session.
- **The consent POST needs a bearer token.** A cross-site form can make the browser send cookies but
  not the web client's access token, so a decision cannot be forged.
- **The cookie is a decision, not a credential.** It is scoped to `/connect/authorize`, lives five
  minutes, is deleted on first use, and only counts for the client it names. Approving one client does
  not approve the next one a link names.
- **Only `planner-mcp` may use the code flow.** The first-party clients keep their direct grants and
  are refused by the consent endpoints, so a consent link cannot mint tokens for them.
- **Every token from this flow is for the MCP resource.** The authorization endpoint refuses a request
  that names no resource, or anything but the configured one; the token's `aud` is that resource and
  carries over on refresh.
- **Claims are rebuilt from the database** at every code redemption and refresh, as for every other
  grant, so a role change or deactivation reaches MCP clients at their next refresh.

## Configuration

| Setting | Default | |
| --- | --- | --- |
| `Planner:Auth:McpClientId` | `planner-mcp` | Public client, PKCE required. |
| `Planner:Auth:McpRedirectUris` | `https://claude.ai/api/mcp/auth_callback` | Matched exactly. |
| `Planner:Auth:McpResource` | `<Issuer>/mcp` | Without it or an issuer, MCP sign-in is refused and a warning is logged at start. |

## Development

`dotnet run --project src/Planner.AppHost` serves the web client on a fixed `http://localhost:5175`,
not behind Aspire's proxy, and hands that address to the API as its issuer. So the dev endpoints an
MCP client needs are:

| | |
| --- | --- |
| Issuer | `http://localhost:5175/` |
| MCP resource | `http://localhost:5175/mcp` |
| Client id | `planner-mcp` |
| Redirect URIs | the MCP Inspector's, `http://localhost:6274/oauth/callback` and `…/debug` |

Vite proxies `/mcp` and `/.well-known` to the API, as Caddy does in production, so discovery and the
endpoint live on the same origin as the sign-in page.

To test a client that cannot reach localhost (claude.ai, for one), put a tunnel in front of port 5175
and point the `public-url` parameter at it:

```
dotnet user-secrets --project src/Planner.AppHost set Parameters:public-url https://<tunnel>/
```

The issuer and resource follow, and Vite accepts the tunnel's host name.

## Known gaps

- **Loopback redirects.** Desktop and IDE clients call back to `http://127.0.0.1:<random port>/…`.
  Redirect URIs are matched exactly, so these need dynamic client registration or client ID metadata
  documents rather than an entry in `McpRedirectUris`.
- **Audience enforcement.** Tokens from this flow carry the MCP audience, but the REST API does not yet
  refuse them. That belongs with the `/mcp` endpoint: it must accept only its own audience, and the API
  must reject tokens whose only audience is the MCP resource.
- **Revoking an assistant.** There is no screen yet listing connected clients. Access ends when the
  account is deactivated, or when the refresh token lapses after `RefreshTokenDays` unused.
