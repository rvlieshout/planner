# Desktop client

An Avalonia 12 client on .NET 10, in `src/Planner.Client`, dressed in
[AtomUI](https://github.com/AtomUI/AtomUI) — an Ant Design component system for Avalonia. It signs in
against the on-prem API, shows a team's board, keeps it live over SignalR, and keeps itself up to date
through Velopack.

## Running it in development

```bash
dotnet run --project src/Planner.AppHost
```

That is the whole stack: database, API and this client, the last as a resource you press **Start** on
in the Aspire dashboard, which opens at <https://localhost:17200>. Setting `Planner.AppHost` as the
startup project in an IDE does the same thing. It launches with `PLANNER_SERVER_URL` pointed at whatever port the API landed
on, so nothing has to be configured by hand.

To run the client on its own against an API you started some other way:

```bash
docker compose up -d           # the API and database
dotnet run --project src/Planner.Client
```

Sign in with the bootstrap owner from your `.env` (`PLANNER_OWNER_EMAIL` /
`PLANNER_OWNER_PASSWORD`). The server field defaults to `http://localhost:8080`, is remembered between
runs, and is overridden for one process by `PLANNER_SERVER_URL`.

Development builds are not Velopack installs, so the update service reports that updates are managed
by your IDE and does not run its loop. Everything else behaves identically. To exercise updates for
real, see [releasing.md](releasing.md).

## Shape

```
Program.cs            VelopackApp.Run() first, then Avalonia
App.axaml.cs          AtomUI registration and the design tokens, then the DI container and window
Services/             the parts with no UI: settings, session, API, realtime, updates
ViewModels/           shell, login, workspace, navigation, board, my issues, new issue, update banner
Views/                one .axaml per view model, matched by ViewLocator; plus the two real
                      windows — MainWindow and the IssueEditorWindow dialog — and DragGhostView,
                      the card that follows the cursor during a drag
Controls/             the Icon control and the named Lucide geometries
Converters/           hex colour to brush
Infrastructure/       app paths, file logging, the Velopack logging bridge
Assets/Icons.axaml    the embedded Lucide glyphs
```

`Planner.Contracts` is referenced directly, so every DTO and the SignalR method names are shared with
the server and checked by the compiler. That project deliberately has no dependencies — the enums were
moved into it precisely so a desktop app would not drag EF Core and ASP.NET Identity along.

Bindings are compiled (`AvaloniaUseCompiledBindingsByDefault`), so a typo in a binding path is a build
error rather than a blank space at runtime.

## Where the client keeps things

Everything lives in `%AppData%\Planner` (`~/.config/Planner` on Linux/macOS), **not** next to the
executable. Velopack installs each version into its own `app-{version}` folder and deletes the old
one, so anything stored beside the binary would vanish on the next update.

| File | Contents |
| --- | --- |
| `settings.json` | Server URL, update feed override, channel, last email, last team |
| `session.dat` | The refresh token. DPAPI-encrypted per user on Windows; owner-only permissions elsewhere |
| `logs\planner-{date}.log` | Client and Velopack messages, interleaved. Kept 14 days |

The log is the thing to ask a user for. It is one file, it covers both the app and the updater, and it
is the only record of what happened on a machine nobody can attach a debugger to.

## settings.json

```jsonc
{
  "serverUrl": "http://planner.internal:8080",

  // Optional. Defaults to {serverUrl}/updates, which is what the API serves.
  // May also be a UNC path or local folder: "\\\\fileserver\\planner\\releases".
  "updateFeedUrl": null,

  // Optional. Defaults to the platform channel ("win"). Set "beta" to put this machine in a pilot group.
  "updateChannel": null,

  "lastEmail": "dana@company.local",
  "lastTeamId": "0199…"
}
```

A standard install needs none of this: one server URL, typed on the sign-in screen, is enough for both
the API and updates.

## Signing in

`password` grant against `/connect/token` with `client_id=planner-desktop`, then `GET /api/v1/me` for
the profile and team list. The refresh token is stored, so the next launch resumes without a prompt;
`AuthService.TryRestoreAsync` fails silently to the sign-in screen if the server is unreachable or the
token has expired.

A 401 on any request triggers exactly one refresh-and-retry. One, not a loop — a refresh token the
server has rejected will not start working on the third attempt, and retrying would only hammer the
token endpoint.

## Navigation

The left sidebar is the whole app's map:

| Row | Shows |
| --- | --- |
| Team switcher | Every team the caller can read. Switching rebuilds the rest of the sidebar. |
| **My Issues** | Everything assigned to you, across *every* team — not just the one on screen. |
| *Team name* | The team board: one column per workflow state. |
| **Projects** | One row per project in the current team, each with the project's own colour. Opens that project's board. |
| Footer | Who you are signed in as, and the way out. |

The client opens on **My Issues**. It is the one view about the person rather than the team, and it is
the question someone opens a tracker to answer. Switching teams keeps the kind of view you were on,
except a project view — that project belongs to the team you just left.

Each row builds its own content view model and cancels whatever the previous one was still fetching,
so clicking through the sidebar quickly cannot leave a slow response overwriting a newer view.

The sidebar is a real Grid column: drag its edge to resize it, or collapse it entirely with Ctrl+B.
`WorkspaceView.axaml.cs` owns that — the column has to go to zero width *and* drop its MinWidth, which
is not something a view model can express.

## Window chrome and keyboard

This is a desktop application, so it wears the furniture of one: a menu bar, a 31px toolbar strip over
the content, a status bar, and modal dialogs in their own windows. Nothing is a floating card, and the
whole shell is built on 22-24px rows rather than the touch targets a stock component library ships
with.

The windows derive from `atom:Window`, which draws its own caption strip: the title, the
minimise / maximise / close buttons, the drag and double-click behaviour and the differences between
platforms all come from the control theme. What the app adds to that strip — the hamburger, the menu,
and the two quick actions that stand in for it while it is folded — goes in the title bar's
`LeftAddOn`.

| Where | What it carries |
| --- | --- |
| Menu bar | File (new issue, refresh, sign out, exit), View (the two views, sidebar toggle), Help (about) |
| Toolbar strip | The current view's name, and its actions: New Issue, Refresh |
| Status bar | What the current view holds, whether the socket is live, and the running version |

| Gesture | Does |
| --- | --- |
| `Ctrl+N` | New issue |
| `F5` | Refresh the current view |
| `Ctrl+Shift+M` / `Ctrl+1` | My Issues |
| `Ctrl+Shift+B` / `Ctrl+2` | The team board |
| `Ctrl+B` | Show or hide the sidebar |
| `Enter`, double-click | Open the selected issue |
| Drag a row | Move it to another column or group, or reorder it in place |
| `Esc` | Close the issue dialog |

Two things about Avalonia are worth knowing before editing those:

- **`MenuItem.InputGesture` only draws the shortcut.** It does not listen for it. Every accelerator is
  declared once in `MainWindow`'s `KeyBindings` and repeated on the menu item for display.
- **Digits must be written `D1`, not `1`.** `KeyGesture.Parse("Ctrl+1")` reads the digit as a `Key`
  *enum value* and yields `Ctrl+Key.Cancel` — no error, no warning, just an accelerator that never
  fires. The number-row shortcuts above are bound as `Ctrl+D1`/`Ctrl+D2`; the menu advertises the
  spelled-out `Ctrl+Shift+…` forms, because a gesture built on `D1` renders as the literal "Ctrl+D1".

## Creating and editing issues

One form, `IssueEditorViewModel`, serves both directions. **New issue** — the toolbar, File ▸ New
Issue, or Ctrl+N — opens it empty; double-clicking a card on a board or a row in My Issues, or pressing
Enter on the selected one, opens it on that issue.

The form is a real modal dialog (`IssueEditorWindow`) with its own title bar, not a panel drawn over a
dimmed page. The view model does not know that: it exposes `Editor`, and `WorkspaceView.axaml.cs`
opens the window when that becomes non-null and closes it when it clears. Closing the window from its
title bar runs `CloseEditorCommand`, or the workspace would still believe a form was open and refuse
to open the next one.

**Creating.** Only the title is required — the server fills in the team's default workflow state, no
priority and no assignee — so an issue can be captured in two keystrokes and fleshed out later. That is
the difference between a tracker people use and one they route around. Everything else is optional:
status, priority, assignee (team members only, which the API enforces), estimate, project, milestone
(filtered to the chosen project) and labels as toggleable chips. Opening the form from inside a project
pre-selects that project.

**Editing.** The form is populated from `GET /api/v1/issues/{id}` — the summary on a card has no
description, so the detail is fetched. On save it sends a **diff**: the values the form held when it
opened are remembered, and only fields the user actually changed are included in the PATCH. Two people
editing different fields of the same issue therefore do not overwrite each other, and an untouched
description cannot be blanked by a form that merely happened to be holding it.

That diff only works because unset `Optional<T>` properties are *omitted* from the JSON rather than
written as null — see `OptionalJson` in `Planner.Contracts`. A converter cannot do this alone: by the
time it runs the property name is already written, so the best it could manage is an explicit null,
which is precisely the opposite of what "absent" means to a PATCH endpoint.

**Archive** sits in the editor's footer. It is the everyday "done with this" action and the one a team
member is allowed to take; deleting outright needs team-lead authority and is deliberately not offered
in the client.

On success the result is applied to the current view immediately rather than waiting for the socket to
echo it back. The upsert is idempotent, so the echo that follows changes nothing. Validation failures
show the API's own wording — "You can only assign issues to members of the issue's team" beats anything
the form could invent.

## Icons

[Lucide](https://lucide.dev) (ISC), embedded as geometry in `Assets/Icons.axaml` rather than taken
from the `Lucide.Avalonia` package, which targets Avalonia 11 while this client is on 12.

`Controls/Icon.cs` renders them directly instead of using `Path` with `Stretch`: a stretched path
scales the outline but leaves the stroke width in the stretched space, so a 14px icon and a 20px icon
would end up visually different weights. Drawing the geometry under a scale transform keeps the stroke
proportional exactly as the SVG does, with the round caps and joins Lucide's look depends on.

`Controls/AppIcons.cs` gives them names (`AppIcons.MyIssues`), because a view model knows which icon
it wants and a resource key cannot be resolved from a binding.

To add one, take the `<path>` data from the Lucide SVG and follow the note at the top of
`Icons.axaml` — a leading relative `m` is absolute in SVG, but not once it follows another sub-path in
the same geometry.

## Density and palette

The design system is AtomUI's, configured in one place: `App.axaml.cs`. Ant Design's stock metrics are
sized for a web page — 32px controls, 6px corners, 14px text — which on a mouse-driven window reads as
a browser rendered inside a frame. Three seed tokens and the Compact algorithm pull the whole system
back to desktop proportions:

```csharp
new ThemeConfigBuilder()
    .WithAlgorithms(appearance, ThemeAlgorithm.Compact)
    .WithToken("ColorPrimary", appearance is ThemeAlgorithm.Dark ? "#6E79F1" : "#5E6AD2")
    .WithToken("FontSize", "12")
    .WithToken("BorderRadius", "3")
```

Because those are *seeds*, everything derived from them follows: every control, every state colour,
both theme variants. There is no palette to maintain — the hand-written light and dark dictionaries the
client used to carry are gone, and `WithFollowSystemThemes` swaps variants with the OS setting while
the app is running.

Views read tokens directly, with the `{atom:SharedTokenResource ...}` markup extension:

| Token | Used for |
| --- | --- |
| `ColorBgContainer` / `ColorBgLayout` | Content surfaces / the chrome behind them: sidebar, toolbar, status bar, board columns |
| `ColorText` / `ColorTextSecondary` | Body text / captions, counts, labels |
| `ColorBorderSecondary` / `ColorSplit` | Panel edges / the one-pixel rules in the status bar |
| `ColorPrimary`, `ColorPrimaryBgHover`, `ColorPrimaryBorder` | Selection, the current-row marker, and all three parts of the drag feedback |
| `ColorError`, `ColorSuccess`, `ColorInfoBg` | Failures, the live indicator, the update banner |

A handful of app classes sit on top, in `App.axaml`:

| Class | Used for |
| --- | --- |
| `atom:ListBox.dense` | The list row reduced to the rectangle around its content, with AtomUI's per-column "No data" placeholder turned off |
| `atom:Button.tool` / `.caption` | Toolbar and title-bar buttons, on AtomUI's `Text` button type |
| `TextBlock.key` | Issue identifiers, in the mono font so a column of them lines up |
| `TextBlock.caption` | Section labels in the sidebar and forms |

The font is the shell font (`Segoe UI Variable Text`, then `Segoe UI`), set through
`WithDefaultFontFamily`, with the bundled Inter as the fallback for machines that have neither. Only
issue keys deviate, and only because they need fixed advance widths.

## The one theming rule to remember

**A local value beats every style setter.** A value written on the element —
`<Border Background="{atom:SharedTokenResource ColorBgLayout}">` — is a local value, and local values
outrank Style setters, *including* the ones a class turns on. So a column written that way can never
be lit up by adding a class to it, and the highlight silently does nothing.

This is why the resting appearance of the board column, the board card, and the My Issues group is set
in `<UserControl.Styles>` rather than on the elements themselves. Both the default and the drag state
are then Style setters, and the more specific one wins. It is the first thing to check when a class
appears to have no effect.

The same rule cuts the other way and is used deliberately in the issue form: the label chips set their
colours locally on `atom:CheckableTag` precisely so that being checked cannot repaint them in the
primary colour, and each label keeps its own.

## The board

The workspace loads the team's workflow states and issues (`?sort=board`), builds one column per
state, and then subscribes to the hub.

Live changes are applied in place rather than by refetching. `Created` and `Updated` are handled as
the same upsert, which makes the client idempotent: a duplicate delivery or a replay after
reconnecting cannot corrupt the view. `Archived` and `Deleted` remove the card. A change for a team
other than the one on screen is ignored.

### Dragging issues

A card can be dragged to another column, or to another position within its own. The drop resolves to an
index, and the two rows it lands between become the `afterIssueId` / `beforeIssueId` anchors of
`POST /issues/{id}/move` — the server takes the midpoint of their ranks, so a reorder writes one row
instead of renumbering the column. Dropping into empty space below the rows appends.

The board moves the card first and tells the server afterwards. A drag that waits for a round trip
before the card lands feels broken, and the realtime echo of the move is the same idempotent upsert as
any other change, so it only confirms what is already on screen. A failure puts the board back the way
the server sees it by reloading.

My Issues can be dragged too, onto another **group**. Its groups are state *types* rather than states,
because those issues come from different teams whose columns do not line up — so the drop resolves to
that issue's own team's first state of the type, the same "Done" the team's board would move it to.
Workflow states are cached per team for that lookup.

Two Avalonia details make the drag work at all, and both are easy to trip over again:

- **ListBox marks `PointerPressed` as handled** while it moves the selection, so a handler attached in
  XAML never runs. `BoardView` and `MyIssuesView` add theirs on the view itself with
  `RoutingStrategies.Tunnel`, which arrives first.
- **`DragDrop.DoDragDropAsync` wants the originating `PointerPressedEventArgs`**, not a position. The
  press is therefore remembered — never handled, so clicking still selects and double-clicking still
  opens — and the drag starts from it once the pointer has travelled 4px.

### What a drag looks like

A drop is a guess until the board answers three questions, so it answers all three at once:

| What you see | Answers | Where it comes from |
| --- | --- | --- |
| A card following the cursor | *What am I carrying?* | `DragGhostView`, parented to the window's overlay layer |
| A rule between two rows | *Where would it land?* | `BoardColumnViewModel.DropIndicatorOffset`, drawn over the list |
| The column tinted and outlined in the primary colour | *Would this column take it?* | `IsDropTarget`, as a class on the column |
| The original row faded to 40% | *Which one is in flight?* | `IssueCardViewModel.IsDragging`, as a class on the card |

The ghost exists because a platform drag owns the cursor: the only thing an app can put underneath it
is something it draws itself. It lives in the overlay layer — above every view, outside every clip —
and is moved from the drag events the views already receive. `BoardView` allows the drop on itself and
listens on the tunnel, so the ghost keeps up even over the gaps between columns, where no column will
raise an event.

The rule and the move come from the same call. `IssueRows.DropTarget` returns the index *and* the
offset the rule is drawn at, out of one walk of the realised containers, so what is shown can never
point at a different gap from the one that is used.

My Issues gets three of the four. A drop there sets an issue's state and does not choose a position, so
there is no gap to point at and no rule to draw. Hovering the group an issue is already in clears the
highlight rather than leaving the last one lit, so "this would do nothing" is visible as the absence of
a target and not only as a cursor glyph.

The dot in the status bar is green while the socket is up and grey when it is not. The board still
works when it is grey — it is simply as fresh as the last fetch. `WorkspaceViewModel` forwards the
current view's own summary ("11 issues in 6 columns") to that bar, because the status bar lives in the
window frame and cannot bind through the content it is describing.

## Updates

Covered in full in [releasing.md](releasing.md). The client-side rules, briefly:

- The check runs **at startup and every four hours**, on a background task, from
  `ShellViewModel.StartAsync` — before and independently of any sign-in.
- An available update is **downloaded immediately** and staged. Nothing restarts on its own.
- The banner sits in the window shell, above whichever screen is showing, **including the sign-in
  screen**. If a release broke sign-in, the person who most needs to know an update exists is the one
  who cannot get past the login box.
- A failed sign-in additionally points at the waiting update, for the same reason.
- A check that fails (no network, feed down, laptop on a train) is logged and retried on the next
  tick. It does not interrupt anyone.

## Adding a screen

1. `ViewModels/ThingViewModel.cs` deriving from `ViewModelBase`.
2. `Views/ThingView.axaml` with `x:DataType="vm:ThingViewModel"`.
3. Register it in `App.BuildServices`.

`ViewLocator` maps `Planner.Client.ViewModels.ThingViewModel` to `Planner.Client.Views.ThingView` by
name, so a `ContentControl` bound to a view model renders the right view with no extra wiring.

## Not built yet

The client is a working foundation, not the finished product. Present: sign-in, session resume, team
switching, the sidebar, My Issues, team and project boards, creating and editing issues in a modal
dialog, archiving, the menu bar and status bar, keyboard activation and a per-row context menu, live
updates, and the full update pipeline. Absent, in rough order of what a user
would miss first:

- **Comments, sub-issues, attachments and the activity feed.** The editor covers an issue's fields;
  everything around the conversation is still API-only (`docs/api.md`).
- Filtering, search and saved views — the API's filter surface is much richer than the UI exposes.
- Project and milestone management, and the document views.
- Offline queueing of writes.
