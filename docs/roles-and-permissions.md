# Roles and permissions

Authority comes from two independent things:

1. **Organisation role** — one per user, stored as an ASP.NET Identity role. It says what someone is
   in the installation as a whole.
2. **Team role** — one per team membership, stored on `team_members`. It says what someone does
   inside one team.

Neither alone decides anything. `TeamAccess` folds the pair into one `TeamPermission` value and every
endpoint asks a single question: *do I have at least X on this team?*

## Organisation roles

| Role | Intended for | Can |
| --- | --- | --- |
| `owner` | The person who installed it | Everything an admin can, plus grant and revoke the `owner` role. Cannot be deactivated. |
| `admin` | IT / operations | Create and deactivate users, create teams, manage organisation-wide labels, and administer **every** team. |
| `member` | Everyone doing the work | Full author rights in the teams they belong to. |
| `guest` | Contractors, stakeholders, other departments | Read and comment, only in teams they were explicitly added to. Never creates or edits content. |

Admins administer every team, including private ones. This is not a hole so much as an admission:
an administrator can add themselves to any team in two API calls, so pretending otherwise buys
nothing but a longer path. The audit trail records who did what either way.

`guest` is a **ceiling**, not a starting point. A guest given the team role `Lead` is still capped at
commenting.

## Team roles

| Role | Can |
| --- | --- |
| `Lead` | Everything a member can, plus team settings, membership, workflow states and team labels — and the hard deletes (delete an issue, project or document outright). |
| `Member` | Create and edit projects, milestones, documents and issues. Archive them. Comment, attach, relate. |
| `Viewer` | Read everything in the team. Comment. Change nothing. |

A team cannot be left without a lead: demoting or removing its only `Lead` returns **409**.

## The permission ladder

`None < Read < Comment < Write < Administer`

| Organisation role | Team role | Resulting permission |
| --- | --- | --- |
| `owner` / `admin` | *(any, including none)* | `Administer` |
| `member` | `Lead` | `Administer` |
| `member` | `Member` | `Write` |
| `member` | `Viewer` | `Read` |
| `guest` | `Lead` / `Member` / `Viewer` | `Comment` |
| any | not a member | `None` |

## What each level unlocks

| Action | Required |
| --- | --- |
| See a team, its projects, issues, documents, activity | `Read` |
| Post a comment, add an attachment | `Comment` |
| Create / edit / archive issues, projects, milestones, documents | `Write` |
| Move issues between columns, assign, relabel, relate | `Write` |
| Team settings, membership, workflow states, team labels | `Administer` |
| Delete an issue, project or document outright | `Administer` |
| Delete a comment you did not write | `Administer` |
| Edit a comment you did not write | **nobody** — authorship is not an administrative power |

Organisation-level actions bypass teams entirely:

| Action | Required |
| --- | --- |
| List users (for assignee pickers) | any authenticated user |
| Create a user, reset a password, deactivate a user | `owner` / `admin` |
| Create a team | `owner` / `admin` |
| Create or edit organisation-wide labels | `owner` / `admin` |
| Grant or revoke the `owner` role | `owner` |

## How it answers

| Situation | Status |
| --- | --- |
| No token, or an expired one | `401` |
| Authenticated, but not a member and not an admin | `404` — the API does not confirm that a team you cannot see exists |
| A member, but the action needs a higher level | `403`, with the detail naming what you have and what is needed |
| A guest attempting to create content | `403` |

## Token flow

```
POST /connect/token
  grant_type=password
  client_id=planner-desktop
  username=<email>
  password=<password>
  scope=openid profile roles offline_access planner.api
      ↓
  { access_token (JWT, 60 min), refresh_token (14 days), token_type: "Bearer" }
```

The access token carries `sub`, `name`, `email` and `role`. Refreshing rebuilds those claims from the
database rather than copying them from the old token, so a role change or a deactivation takes effect
at the next refresh rather than at the next sign-in.

`planner-desktop` is a **public** client: a binary on every workstation cannot keep a secret, so it
has an identifier, not a credential. The user's password is the credential. The token endpoint is
rate-limited to 20 requests per minute per IP, and Identity locks an account for 15 minutes after 10
failed attempts.

### Service integrations

There is no `client_credentials` grant. A token with no user behind it cannot answer the question
every authorization check starts from — *which teams is this account in?* Give the integration its own
user account (`member` or `guest`), add it to the teams it needs, and let it use the password grant.
It then shows up in the audit trail like anyone else, and can be deactivated the same way.

### Upgrading to authorization code + PKCE

If you later put a browser in front of this (a web client, or an on-prem SSO portal):

1. In `AuthenticationSetup`, add `options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()`
   and `SetAuthorizationEndpointUris("connect/authorize")`.
2. Add cookie authentication and a login page — OpenIddict's authorize endpoint needs an interactive
   session to consent from.
3. Add `Permissions.GrantTypes.AuthorizationCode`, `Permissions.Endpoints.Authorization` and the
   client's redirect URIs to `OpenIddictClientSeeder`.
4. Drop `AllowPasswordFlow()` and the password permission once every client has moved.

Nothing in the permission model changes — only how a token is obtained.
