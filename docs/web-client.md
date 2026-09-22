# Web client

A SvelteKit 2 / Svelte 5 application in `client/`, compiled to static files and served from the same
origin as the API. It signs in against the on-prem API, shows a team's board, keeps it live over
SignalR, and needs nothing installed.

## Why it is shaped this way

Three decisions account for most of what follows.

**It is a static bundle, not a server.** `adapter-static` writes one shell and a pile of hashed
assets. There is no Node process in the deployment, nothing to patch at runtime, and a deploy is a
container image that copies files. The whole application is routed in the browser.

**It is served from the API's own origin.** Caddy serves this app at `/app` and proxies everything it
does not recognise to the API container. So `/api/v1/issues`, `/connect/token` and `/hubs/planner`
are same-origin paths. There is no CORS to configure, no origin list to keep in step with a domain,
no preflight on every write, and no second hostname to certificate. `Planner__Auth__AllowedOrigins`
stays empty and is not needed.

**Nothing is fetched from anywhere else.** The typefaces are npm packages compiled into the app's own
assets, and the icons are vendored into a generated module. There is no CDN, no font service and no
analytics — an installation behind an air gap renders exactly as one on the internet does.

## Running it in development

```bash
dotnet run --project src/Planner.AppHost
```

That is the whole stack: database, API, and this client on Vite's development server, which the Aspire
dashboard lists as `client`. The app host passes `PLANNER_SERVER_URL` pointed at whatever port the
API landed on, and `vite.config.ts` proxies `/api`, `/connect` and `/hubs` to it — so development
looks like production, where Caddy does the same job.

On its own, against an API started some other way:

```bash
docker compose up -d db api
cd client
npm install
npm run dev                      # http://localhost:5175/app
```

`PLANNER_SERVER_URL` overrides the proxy target for one run; it defaults to `http://localhost:8080`.

To check the real thing rather than the development server, `docker compose up -d --build` serves the
whole stack through the deployed image at <http://localhost:8081/app>.

| Command | |
| --- | --- |
| `npm run dev` | Vite, with the API proxied |
| `npm run build` | Static output into `client/build` |
| `npm run preview` | Serves that output |
| `npm run check` | `svelte-check` — TypeScript and every binding in every component |
| `npm run icons` | Regenerates `src/lib/icons/icons.ts` from `lucide-static` |

## Shape

```
client/
  svelte.config.js       adapter-static, base path /app
  vite.config.ts         the development proxy, and the version stamp
  scripts/build-icons.mjs  vendors the Lucide glyphs this app uses
  src/
    app.html             the shell every route is answered with
    lib/
      api/               types.ts (the contract), http.ts (one fetch), index.ts (the endpoints)
      auth/              tokens.svelte.ts (the grant), session.svelte.ts (who, and what they may do)
      realtime/          one SignalR connection for the whole application
      components/        primitives, then shell/, issues/, projects/
      styles/            fonts.css, tokens.css, app.css
      board.ts           how lanes are laid out
      issues/move.ts     what a drop writes, shared by the board and list views
      markdown/          the markdown pipeline, and resolving attachment references
      dnd.svelte.ts      dragging issues
      chrome.svelte.ts   what the window frame is currently saying
      navigation.svelte.ts  the unsaved-work guard
      workspace.svelte.ts   teams, the current team, and its cached states/members/labels
    routes/
      +layout.svelte     theme, session, the two hosts
      login/
      (app)/             everything behind sign-in
```

State is Svelte 5 runes throughout. The stores are plain classes with `$state` fields, exported as
singletons — there is no store library and no context plumbing, because the things that are global to
this application genuinely are global: who is signed in, which team is on screen, whether the socket
is up.

### The contract

`src/lib/api/types.ts` is a hand-written mirror of `Planner.Contracts`, ordered to match the C# files
it mirrors so a contract change is a diff in the same shape. This is the one real seam in the design:
the desktop client references that project directly and is checked by the same compiler, and a browser
cannot do that. `npm run check` is what stands in for it — it type-checks every call site against
these shapes — and `docs/api.md` remains the authority when the two disagree.

Two server conventions the types depend on:

- **Enums travel as names.** `"Urgent"`, never `1`, over REST and over the socket alike.
- **PATCH is a true patch.** An absent key is untouched, an explicit `null` clears the field. In
  TypeScript that falls out of `?:` plus `| null`, where C# needs `Optional<T>`.

## Signing in

`password` grant against `/connect/token` with `client_id=planner-web` — a second public client,
registered beside `planner-desktop` by `OpenIddictClientSeeder`, so the two can be told apart in the
logs and revoked independently. Then `GET /api/v1/me` for the profile and team list.

The refresh token is kept in `localStorage`, so the next visit resumes without a prompt. That is the
one real difference from the desktop client, which encrypts it into a file only its own user can read;
a browser has no such vault, and the mitigation is that the application is deliberately first-party —
its own origin, no third-party script, no CDN, so nothing else runs where it could be read. The access
token is never written down at all: it lives in memory for the lifetime of the page.

A 401 on any request triggers exactly one refresh-and-retry. One, not a loop — a refresh token the
server has rejected will not start working on the third attempt, and retrying would only hammer the
token endpoint. If that refresh fails, the session ends and the shell routes to sign-in.

There is no server field on the sign-in screen. The desktop client needs one because it is a binary
that could be pointed anywhere; this application was served by the installation it talks to.

## Navigation

The left sidebar is the whole application's map, and matches the desktop client's:

| Row | Shows |
| --- | --- |
| Team switcher | Every team the caller can read. Switching rebuilds the rest of the sidebar. |
| **Users & access**, **Teams** | Administration, for the people entitled to it. Reachable on an installation with no teams at all — creating the first one is the point. |
| **My Issues** | Everything assigned to you, across *every* team. The application opens here. |
| *Team name* | The team board. |
| **Projects** | One row per project in the current team, in its own colour. The ＋ starts a new one. |
| Footer | Who you are signed in as, and the way out. |

Routes are `/app/my-issues`, `/app/board`, `/app/projects/[id]`, `/app/projects/[id]/settings`,
`/app/issues/[key]`, `/app/teams`, `/app/users`, `/app/settings`. Issues are addressed by **key**
rather than id, so `ENG-42` is a URL someone can paste into a chat — which is the thing a desktop
application could never offer.

### Leaving a page with unsaved work

A modal cannot be navigated past. A page can, so the router asks first.

A page that can be dirty publishes `chrome.unsavedWork`, returning a summary of what stands to be lost
in the words the prompt will use — "this issue's edits and an unposted comment". A board or a list
never sets it and is never interrupted. `installNavigationGuard` in `navigation.svelte.ts` is the whole
guard, and every route that replaces the content pane goes through it, including the browser's own
back button: the navigation is cancelled, the question asked, and the navigation repeated on a yes —
cancelling alone would leave the address bar one step ahead of the page.

Closing the tab is the browser's own prompt, and nothing here decides its wording.

The safe answer is the default one: **Keep editing** takes the focus, and Escape, the backdrop and the
title-bar close all resolve to it.

## Window chrome and keyboard

It wears a desktop application's furniture: a title bar with the application menu, a 36px toolbar strip
over the content, a status bar, a sidebar you can drag or collapse, and modal dialogs in the top layer.
The whole shell is built on 28px rows rather than the touch targets a component library would ship.

The menu bar is folded into one button rather than spread across the title bar. A browser tab already
has a menu bar at the top of the window, and a second row of words under it reads as a page imitating
an application rather than as one. The commands are grouped as a menu bar would group them, and
captioned with the shortcuts that also work without opening it.

The same commands are searchable in the command palette (`Ctrl+K`, or the **Commands** button in the
title bar), which doubles as the keyboard reference: opened with nothing typed, it lists what works
on this screen right now — the page's own commands first, then the shell's, then every project. The
palette is Bits UI's `Command` inside a native `<dialog>`.

Menu, palette and keyboard read one list (`commands.svelte.ts`). The shell contributes what is always
there; a page adds its own through `chrome.set({ commands })`. A command that is listed is a command
that works, and a shortcut that is captioned is a shortcut that is bound.

| Gesture | Does |
| --- | --- |
| `Ctrl+K`, `?` | Command palette and shortcut list |
| `C` | New issue (in the open project, on a project page) |
| `Shift+P` | New project |
| `F5` | Refresh the current view |
| `G` then `I` | My Issues |
| `G` then `B` | The team board |
| `G` then `U` / `T` / `S` | Users & access / Teams / Preferences |
| `Ctrl+B` | Show or hide the sidebar |
| `Ctrl+S` | Save — an issue, a team, a user, your profile |
| `Shift+C` | New sub-issue, on an issue |
| `G` then `P` | The parent issue, on an issue |
| `O` | Show or hide the overview, on a project |
| `E` | Edit the description, on an issue |
| `V` | Switch a board between columns and rows |
| `Ctrl+Enter` | Save, in a form |
| `Enter`, double-click | Open the selected issue |
| Drag a row or a card | Move it to another column or group, or reorder it in place |
| `Esc` | Close the dialog |

**The desktop client's accelerators do not carry over.** `Ctrl+N`, `Ctrl+Shift+N`, `Ctrl+T`, `Ctrl+W`
and `Ctrl+1…9` never reach a page: the browser opens its window or switches its tab before any script
sees the key. So the web client's verbs are single keys and `G` sequences, and only keys a page is
actually handed carry a modifier. On a Mac, `Ctrl` is `⌘`.

Single-key shortcuts do not fire while a field has focus, or they would eat what is being typed;
`Ctrl` chords and `F5` do. Nothing fires while a modal is open. `F5` is bound only where the open page
knows what refreshing means; everywhere else the browser's own reload is the right answer.

The page's heading, its toolbar actions and its one-line status are published through `chrome`
(`chrome.svelte.ts`) and read by the shell. The describing furniture lives outside the thing it
describes, and that is the only arrangement that works.

## The board

The workspace loads the team's workflow states and issues (`?sort=board`), builds one column per
state, and subscribes to the hub.

### Two views of it

A team board and a project board are drawn either as columns of cards (`BoardView`) or as groups of
rows (`ListView`), switched from the toolbar or with `V`. The list is the same issues in the same
groups, in the same order the columns stand in — Todo still before Backlog — drawn as the dense rows
My Issues uses. It is the view for reading a board rather than pushing it along: every title in full,
scanned down rather than across.

The choice is one preference for every board, kept in `localStorage` by `settings.svelte.ts`. Someone
who reads boards as lists reads all of them that way, and a preference that has to be set again on
each project is one nobody sets at all.

Dragging works the same in both, because both hand the drop to `moveIssue` in `src/lib/issues/move.ts`
— the same states, the same ranks, the same request.

### Lanes, and the one that holds two columns

Columns are laid out one per lane, with a single exception: **Todo sits on top of Backlog in one
lane**. What a board is asked to do most often is promote something out of the backlog, and stacking
the two makes that a drag straight up into the column above rather than a hunt for one off to the
right.

The two stay two columns while they do it — their own headers, counts and drop targets — because they
are still two workflow states and a drop has to land in one of them. They split the lane's height
evenly, so both halves stay on screen and the drag always has somewhere to go. The pairing is by
`WorkflowStateType` rather than by name, it covers every state of those two types, and the lane takes
the leftmost of the positions those states hold. `layOut` in `src/lib/board.ts` is the whole rule.

### Dragging issues

Built on pointer events rather than HTML5 drag-and-drop, which owns the drag image and gives you a
washed-out screenshot you cannot style, size or replace. A pointer drag draws its own card, which is
the only way to answer all four questions at once:

| What you see | Answers |
| --- | --- |
| A card following the cursor | *What am I carrying?* |
| A rule between two rows | *Where would it land?* |
| The lane outlined and the column tinted | *Would this column take it?* |
| The original row faded to 40% | *Which one is in flight?* |

Targets are found by hit-testing the DOM: a column marks itself `data-drop-key` and its rows
`data-issue-id`, and that is the whole contract. One walk of the rows produces both the drop index and
the offset the rule is drawn at, so what is shown can never point at a different gap from the one that
is used.

The drop resolves to the two rows it landed between, which become the `afterIssueId` / `beforeIssueId`
anchors of `POST /issues/{id}/move`. The server takes the midpoint of their ranks, so a reorder writes
one row. The card moves first and the server is told afterwards: a drag that waits for a round trip
before the card lands feels broken, and the realtime echo is the same idempotent upsert, so it only
confirms what is on screen. A refusal puts the board back.

The list view of a board drags the same way, between its groups and within them: its groups are that
team's own states, so a row landing in one means exactly what a card landing in that column means.

My Issues can be dragged too, onto another **group**. Its groups are state *types* rather than states,
because those issues come from teams whose columns do not line up — so the drop resolves to that
issue's own team's first state of the type. There is no position to choose there, so no rule is drawn.

## Descriptions and comments

Descriptions and comments are **markdown**, stored as the text that was typed and rendered with
[Carta](https://github.com/BearToCode/carta) — remark and rehype underneath. An issue opens with its
description **rendered**; clicking it, pressing `E` or using the Edit button turns that block into the
editor, and `Escape` goes back to reading.

Nothing saves on its own. The editor is bound to the same state the old textarea was, so a description
is part of the issue's unsaved work exactly as before: `Ctrl+S` writes it with everything else, and
leaving with it half-written raises the same prompt. Comments keep their own model — `Ctrl+Enter`
posts, and editing one has its own Save.

The same treatment is on comments and on a project's description. The quick-create dialog keeps a
plain textarea: there is nothing to render yet in a form that is creating the thing.

### Images, and the `attachment:` reference

Choosing Add images, pasting, or dropping an image into a description or comment uploads it to the issue and writes a
reference to it:

```
![screenshot.png](attachment:0199ab…)
```

The reference rather than a URL, because the bytes sit behind the issue's team permission and are
fetched with a bearer token — which an `<img>` cannot send, since the browser makes that request, not
this app. So the pipeline moves the reference onto `data-attachment` before sanitising (an unknown
URL scheme would not survive it), and `resolveAttachments` fills in the `src` from a blob fetched the
ordinary way. Blobs are cached per attachment for the session and revoked on sign-out.

Each mounted editor owns its Carta instance, including its selection and undo history. Uploads insert at the selection and show progress or an inline error. Editing and saving that field pause until the upload finishes.

A pasted image is an ordinary attachment, so it also appears in the issue's Attachments list — at
once, before anything is saved, because the editor announces each upload and the issue page adds it.
Delete it there and the reference renders as "attachment unavailable" rather than as a broken image.

An image is drawn no wider than the text and, by default, no taller than 360px. Clicking one in the
editor's Preview puts a size bar over it — Auto, S, M, L, Full — and the choice is written into the
reference itself (`attachment:0199ab…#size=m`), so it is plain markdown that undoes like typing.
Clicking an image while reading opens it in a lightbox, fitted to the screen or at its actual size.

A project description has no issue behind it and therefore nowhere to put a file, so it says so
instead of swallowing the paste.

**Everything rendered is sanitised**, on every surface, by the one DOMPurify configuration in
`lib/markdown/carta.ts`: scripts, iframes, forms, event handlers and inline styles go; links are
forced to `target="_blank" rel="noopener noreferrer"`. The markdown pipeline is imported only by the
three routes that show a document, so the board and the lists never load it.

## Creating and editing issues

One form, both directions, in a real `<dialog>`: the top layer, a focus trap, inert content behind it,
and Escape arriving as `cancel` rather than as a key to listen for globally.

The shape is a title, a description, and one wrapping row of **property chips** with no caption column.
The icon and the words in a chip are its label, which only works if a chip always reads as a value —
so every optional property has an explicit empty option, "No priority", "Unassigned", "No project".
Those earn their place twice over: they are also the only way *back* to empty.

**Creating.** Only the title is required — the server fills in the team's default state, no priority
and no assignee — so an issue can be captured in two keystrokes and fleshed out later.

**Editing.** Populated from `GET /api/v1/issues/{id}`, because the summary on a card has no
description. On save it sends a **diff**: the values the form held when it opened are remembered, and
only fields the user actually changed are included. Two people editing different fields of the same
issue therefore do not overwrite each other, and an untouched description cannot be blanked by a form
that merely happened to be holding it.

**Archive** sits in the footer. It is the everyday "done with this" and the action a team member is
allowed to take; deleting outright needs team-lead authority and is deliberately not offered.

## The issue page

Opening a card or a row opens `/app/issues/{key}` — a breadcrumb and a save strip above two
independently scrolling columns. The issue on the left: title, description, sub-issues, relations, the
conversation. Its properties, labels and files on the right.

It saves a diff for the same reason the dialog does. What is different is that the page stays open
while other people work on the same issue, so a refresh driven by the socket moves the *baseline* the
diff is measured against while leaving any field the user has touched alone — and never replaces an
unposted comment or a half-typed attachment link.

Comments load every page rather than the first: a thread is read top to bottom, and "load more" on a
discussion of eleven replies is a button that exists only because the list was paged for a different
reason. The page subscribes to the issue's own SignalR group while it is open, and unsubscribes when
it closes — issue-local traffic is opt-in because it is the high-volume kind.

Files go both ways the server supports them: uploading sends the bytes to
`POST /api/v1/issues/{id}/files`, which keeps them outside the web root and behind the issue's team
permission; linking records a location — a share, an object store — and stores metadata only. Only the
uploaded kind can be downloaded from here, because the other is somewhere this application has never
been.

## Projects, teams and users

**Projects** get a page, not a dialog. An issue is one answer; a project is created and then kept, and
its milestones are each their own resource on the server. A successful create turns the page into that
project's settings page rather than closing it. **Save changes saves the page** — the project's fields,
every dirty milestone row, and anything typed into the add row and not yet added. It has to: the footer
shows one unsaved mark covering all of that, and a button called "Save changes" that leaves the mark
standing is a button that lies. Rows the server refuses keep their own error and stay dirty, and the
page reports how many rather than claiming success over work still sitting there.

**Teams** lists the teams you may *change*, which is a shorter list than the switcher above it.
Archived teams are listed last rather than left out, because restoring one is only possible from a list
that still shows it. The key is set once, at creation, and validated here before the request goes out,
because a rejected key is otherwise a round trip to learn a typo. Two refusals belong to the server and
are shown as it writes them: a duplicate key, and demoting or removing a team's only lead.

A team's **workflow states** — its board's columns — are edited on the same page, one at a time like
its labels: name, type, colour, and whether new issues start there. Move up and down reorders the
board; every state whose position is not its index is renumbered, because positions are whatever the
API was last given and a swap of two tied ones would move nothing. The type is spelled out in the
editor because it is what the application reads: it decides whether an issue counts as done in a
rollup, which states the board stacks into one lane, and where My Issues groups it. The default is
only ever moved, never cleared. Deleting is refused by the server while issues are still in the state,
or when it is the team's last one, and the refusal is shown as it is written.

**Users & access** covers the directory including inactive accounts, and previews *effective* access
per team using the same ladder the server computes — admins and owners administer every team, guests
are capped at commenting whatever their team role says, a member's Viewer role is read-only. Saving is
the account followed by the changed memberships, which are separate API operations; a team change that
fails is reported and its edit kept for another try.

The API remains the authority for every write. What the permission model decides here is only what the
application *offers*, because a button nobody may press is worse than no button.

## Design system

Custom CSS. No framework, no utility classes, no component library — `src/lib/styles/tokens.css` is
the whole system and every component reads from it.

Proportions are a desktop application's rather than a web page's: 13px text, 4px corners, 26px
controls, 28px rows. Ant Design's stock 32px controls and 14px text are sized for touch and a reading
column, and at this density a working board fits on a laptop screen.

| Token group | Used for |
| --- | --- |
| `--bg-app` / `--bg-surface` / `--bg-sunken` | The chrome behind content / content surfaces / board columns and wells |
| `--fg` / `--fg-secondary` / `--fg-tertiary` | Body text / captions / counts and timestamps |
| `--border` / `--border-strong` / `--split` | Panel edges / control edges / the one-pixel rules |
| `--accent` and its subtle, border and ring variants | Selection, the current row, and all three parts of the drag feedback |
| `--success` `--warning` `--danger` `--info` | State, each with a background and a border |

Light and dark differ only in those values; no component redefines a colour for a theme. Which set is
live is decided by `data-theme` on `<html>` — "light" or "dark" when the user has chosen, absent when
they have not, in which case `prefers-color-scheme` decides. Both rules are written out, so an explicit
choice beats the media query in either direction.

**One naming rule, learned the hard way.** `app.css` carries global utility classes, and a component
that gives an element a class of the same name inherits that rule as well as its own. `.field` is a
label stacked over a control in a column; a `<button class="trigger field">` picked the column
direction up and laid its glyph, value and caret out one above the other. The select's variant class
is `as-field` for that reason. Prefix a component's variant classes rather than naming them after a
utility.

## Type and icons

**Inter** for text, **JetBrains Mono** for issue identifiers, which need fixed advance widths so a
column of them lines up. Both are npm packages, declared in `src/lib/styles/fonts.css` and compiled
into the app's own assets — a browser fetches them from the origin it fetched the page from. Only the
latin and latin-ext subsets ship; the full families carry Cyrillic, Greek and Vietnamese as well, which
is four more files per weight nobody here is reading.

**Lucide** (ISC) for icons, vendored by `scripts/build-icons.mjs` into `src/lib/icons/icons.ts` — the
inner markup of each glyph, committed. `Icon.svelte` draws them inline, so an icon inherits
`currentColor` and follows the theme without a second asset per variant, and costs no request at all.
The stroke is scaled with the size the way Lucide's own SVGs do, so a 14px icon and a 20px one are the
same visual weight. To add one: put its name in `ICONS`, run `npm run icons`, commit the result.

## Live updates

One connection for the whole application, because the server puts a connection into a group per team
the caller can read and publishes each change to exactly one of them — a second connection would
receive the same events again, not different ones.

The browser cannot set an `Authorization` header on a WebSocket handshake, so the token goes in the
query string and the API's `SignalRAuthenticationMiddleware` lifts it back into a header before
authentication runs. `accessTokenFactory` is asked again on every reconnect, which is what keeps a
long-lived socket working across an access token's expiry.

Where the desktop client subscribes to issue traffic alone, this one wires all of it: a team someone
renames, a project someone creates, a column someone adds and a member someone removes all land
without a reload. A reconnect refetches rather than resumes — the connection was deaf for as long as it
was down.

The dot in the status bar is green while the socket is up and grey when it is not. Everything still
works when it is grey; the view is simply as fresh as its last fetch.

## Building and deploying

`npm run build` writes `client/build`: one `index.html` shell, hashed assets, and Brotli and gzip
copies of each. `deploy/web.Dockerfile` builds that in a Node stage, copies it into a Caddy image
beside the Astro website, and bakes `deploy/Caddyfile` in — so the routing rules version and roll back
with the image.

```
web image (Caddy)
 ├─ /                 → the website
 ├─ /app/*            → this client, with the shell answering unknown paths
 └─ everything else   → reverse_proxy api:8080
```

`_app/immutable/*` is content-hashed and cached for a year; the shell is never cached, or a deploy
would reach nobody until a browser decided to look again.

There is nothing to install and nothing to update. A deploy is the new bundle, and the next page
load has it.

## Accessibility

Every control is a real element: buttons are `<button>`, the modal is `<dialog>`, the pickers carry
`listbox`/`option` roles and full keyboard navigation, and the sidebar splitter is a focusable
`separator` that arrow keys resize. Focus is visible everywhere, and
`prefers-reduced-motion: reduce` turns the transitions off.

## Not built yet

- **Filtering, search and saved views.** The API's filter surface is much richer than the UI exposes;
  only the issue picker on the relations panel uses `?search=`.
- **Documents.** `GET /api/v1/documents` is complete on the server and unused here.
- **The activity feed.** Read on the issue page only; `/api/v1/activity` across teams has no view.
- **Archiving and restoring projects,** which needs somewhere to see archived ones first.
- Offline queueing of writes.
