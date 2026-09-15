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
ViewModels/           shell, login, workspace, navigation, board, my issues, issue editor,
                      project editor, users and access, teams and membership, update banner
Views/                one .axaml per view model, matched by ViewLocator; plus the real windows —
                      MainWindow, the IssueEditorWindow dialog, About and Confirm — and
                      DragGhostView, the card that follows the cursor during a drag
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
  "serverUrl": "https://planner.lyste.net",

  // Optional. Defaults to https://planner.lyste.net/updates.
  // May also be a UNC path or local folder: "\\\\fileserver\\planner\\releases".
  "updateFeedUrl": null,

  // Optional. Defaults to "win", the stable channel. Set "win-beta" to put this machine in the
  // pilot group; it then reads releases.win-beta.json from the same feed.
  "updateChannel": null,

  "lastEmail": "dana@company.local",
  "lastTeamId": "0199…"
}
```

A standard install needs nothing configured: the sign-in screen offers `https://planner.lyste.net`
and updates come from `https://planner.lyste.net/updates` independently of that address. Missing, null or blank
`updateFeedUrl` values use this default; an explicitly configured custom feed is preserved.

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
| *Team name* | The team board: one column per workflow state, with Todo and Backlog sharing a lane. |
| **Projects** | One row per project in the current team, each with the project's own colour. Opens that project's board. The ＋ on the section header starts a new one. |
| Footer | Who you are signed in as, and the way out. |

The client opens on **My Issues**. It is the one view about the person rather than the team, and it is
the question someone opens a tracker to answer. Switching teams keeps the kind of view you were on,
except a project view — that project belongs to the team you just left.

Each row builds its own content view model and cancels whatever the previous one was still fetching,
so clicking through the sidebar quickly cannot leave a slow response overwriting a newer view.

What the content pane can hold is `IWorkspaceContent`: a title and subtitle for the toolbar strip, a
line for the status bar, and a way to fill itself. Pages made of issues implement `IIssueContent` on
top of that, which adds the click-to-open event and the live-update entry point. The project page is
a form, not a list of issues, and implementing those two as no-ops to satisfy one interface would be
a page pretending it can show something it cannot.

### Leaving a page with unsaved work

A modal cannot be navigated past. A page can, so the navigator asks first.

A page that can be dirty implements `IUnsavedWork` — `HasUnsavedChanges`, and an `UnsavedSummary` that
says what stands to be lost in the words the prompt will use. A board or a list does not implement it
at all and is never interrupted. `WorkspaceViewModel.MayDiscardAsync` is the whole guard, and every
route that replaces the content pane goes through it:

| Route | Asks |
| --- | --- |
| A sidebar row, View ▸ My Issues / Board | Yes |
| Project ▸ New Project, Project ▸ Settings, the sidebar ＋, the toolbar's Project button | Yes |
| The project page's own **Close** button | Yes — it routes back through the same command |
| Switching team | Yes, and the combo box goes back if the answer is no |
| Refresh (F5) | Yes — reloading discards edits as thoroughly as leaving does |
| Sign out | Yes |
| Closing the window, or the updater restarting the app | **No** |

The question reaches the user through `WorkspaceViewModel.ConfirmDiscard`, a `Func<string, Task<bool>>`
the view sets — the same shape as `PlannerApiClient.OnUnauthorized`, and for the same reason: the view
model knows when to ask, and only the layer above it can put a window on the screen. Left unset, in
the previewer or a test, navigation is never interrupted; there is nobody to ask.

`ConfirmWindow` is that window. Its safe answer is its default one: **Keep editing** is both the
default and the cancel button, so Enter, Escape and the title-bar close all keep the work, and only a
deliberate click on **Discard** throws it away.

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
| Menu bar | File (new issue, refresh, sign out, exit), View (the two views, sidebar toggle), Project (new project, project settings), Help (about) |
| Toolbar strip | The current view's name, and its actions: Project — only while one is on screen — New Issue, Refresh |
| Status bar | What the current view holds, whether the socket is live, and the running version |

| Gesture | Does |
| --- | --- |
| `Ctrl+N` | New issue |
| `Ctrl+Shift+N` | New project |
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

### The shape of it

Title, description, and then one wrapping row of **property pills** — no caption column.

```
CORE │ CORE-284
───────────────────────────────────────────────────────────
Nightly import silently drops rows over 4MB     ← borderless
The importer streams into a buffer sized from…  ← borderless, takes the star row

● In Progress ▾  ⚠ Urgent ▾  (DW) Dana Whitfield ▾  ● Apollo ▾  Dual-write ▾  5 points ▾
🏷 bug  performance  needs-design  good-first-issue
───────────────────────────────────────────────────────────
Archive                              [Save changes] [Cancel]
```

Each pill is a real `atom:ComboBox` — selection, keyboard, type-ahead — wearing the shape of a chip:
`StyleVariant="Filled"`, `SizeType="Small"`, `MinWidth="0"` so it is as wide as whatever it currently
holds rather than as wide as a column. One `ItemTemplate` serves both the closed pill and the dropdown
row, so a property looks the same wherever it is read.

The icon and the words in the pill **are** the label. That only works if a pill always reads as a
value, which is why every optional property has an explicit empty option — "No priority",
"Unassigned", "No project", "No milestone", "No estimate" — rather than an empty box. Those options
earn their place twice over: they are also the only way *back* to empty. Before this the assignee,
project and milestone combos could be set but never cleared, because there was no row to choose that
meant "none".

The empty option is not the same thing as no selection, and the view model keeps them apart. Refilling
a combo's `ItemsSource` — which changing project does to the milestone list — makes it write a null
selection back down the binding; that reads as *unchanged*, while the explicit empty option reads as
*clear it*. Collapsing the two would make a field unclearable again. Milestones therefore reset to
`MilestoneOption.None` explicitly when the project changes, because the milestone the form was holding
belonged to the project that was just swapped out.

**Estimate** is a combo on the usual 1/2/3/5/8/13/21 scale rather than a numeric spinner: a spinner
showing a bare number needs a caption to say what the number means, which is the thing this layout is
trying to stop doing. Anything the server sends that is not on the scale is inserted into it on load,
so opening an issue pointed by some other means and saving it cannot quietly round the estimate away.

**Assignees** are drawn with `atom:Avatar` initials, not Gravatar: this app makes no outbound calls,
and `TeamMemberDto.AvatarUrl` is the on-prem hook if a real picture is ever wanted.

**Creating.** Only the title is required — the server fills in the team's default workflow state, no
priority and no assignee — so an issue can be captured in two keystrokes and fleshed out later. That is
the difference between a tracker people use and one they route around. Assignees are team members
only, which the API enforces, and milestones are filtered to the chosen project. Opening the form from
inside a project pre-selects that project.

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

## Creating and editing projects

Projects get a **page**, not a dialog. An issue is one answer and a modal is the right shape for it; a
project is a thing you create and then keep — it carries milestones, each its own resource on the
server, and maintaining those is a session rather than a single answer. So `ProjectEditorViewModel`
is content like any other view: it takes the content pane, sits under the same toolbar and status bar,
and stays open while the work is done.

Get there by the sidebar's ＋ on the PROJECTS header, **Project ▸ New Project** (Ctrl+Shift+N), or —
for one that exists — the **Project** button in the toolbar strip, which appears only while a project
is the thing on screen. **Project ▸ Project Settings** does the same from the keyboard and is disabled
when nothing is selected.

**Creating.** Only the name is required; status, health, lead, colour and the two dates all have
defaults. A successful create turns the page into that project's settings page rather than closing it:
the id arrives, the heading changes, and the milestone section — which needs something to POST to —
comes to life underneath. That is the whole reason this is a page. **Close** then lands on the new
project's board, empty and waiting for its first issue.

**Editing.** Populated from `GET /api/v1/projects/{id}`, saved as a **diff** for the same reason the
issue form sends one: only fields the user actually changed are included in the PATCH, so two people
editing different fields of the same project do not overwrite each other. Renaming or recolouring
refreshes the sidebar row and the toolbar heading without a reload.

**Milestones** are listed on the same page and edited in place: name, target date and status, with the
issue rollup the server computes beside them. A milestone is its own POST, PATCH and DELETE on the
server, so each row owns its request, its busy state and its error, and its tick appears only once that
row differs from what the server holds — which makes the tick the row's unsaved mark as well as its
save button.

What a row does not own is *when* it is saved. **Save changes saves the page**: the project's own
fields, every dirty milestone row, and anything typed into the add row and not yet added. It has to.
The page shows one unsaved mark covering all of that, and a button called "Save changes" that leaves
the mark standing is a button that lies. Rows are saved one at a time after the project; any that the
server refuses keep their own error and stay dirty, and the page then reports how many rather than
claiming success over work still sitting there.

Deleting asks first, and says what it costs: the milestone goes, its issues stay in the project.

**Colour** is ten swatches plus a hex box. The swatches are the quick answer and the box is the honest
one, because the server takes any hex and a team may have a colour of its own. Selection is a ring
around the swatch rather than a tick drawn on it, which would be invisible on the pale half of the
palette.

The footer says **Unsaved changes** whenever anything on the page differs from what the server holds:
the form itself, a milestone row edited but not saved, or a milestone typed into the add row and never
added. That is the same `HasUnsavedChanges` the navigator's guard reads, computed from the fields
rather than tracked alongside them, so the mark and the prompt can never disagree. Rows are watched by
virtue of being in the `Milestones` collection rather than by having been added through the right
helper — one path appending a row directly would otherwise leave its edits invisible to both.

Because it is computed, it is read at moments a form is not usually inspected: on *every* property
change, including the ones a control makes on its own behalf. Emptying the collection a combo box
draws from — which reloading the team's members does — makes that box write a null selection back
down the binding, and clearing a text box writes null rather than `""`. So nothing in the dirty check
dereferences a bound value directly; each control is read through one accessor that treats "holding
nothing" as *unchanged*, and those same accessors build the request, so the check and the PATCH cannot
drift apart. The one distinction that has to survive is the lead: no selection means unchanged, while
the **No lead** option — a selection with no member behind it — means clear it.

**Archiving a project** is deliberately not offered: the client only lists live projects, so archiving
one from this page would strand it with no way back.

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
| `atom:ComboBox.pill` | The issue form's property controls: filled, small, and sized to their content, so the value is its own label |
| `atom:Button.tool` / `.caption` | Toolbar and title-bar buttons, on AtomUI's `Text` button type |
| `TextBlock.key` | Issue identifiers, in the mono font so a column of them lines up |
| `TextBlock.caption` | Section labels in the sidebar and forms |
| `atom:Avatar.xs` / `.sm` / `.md` | Monogram avatars at 16 / 20 / 22px, each with the font size that fits inside it |

The font is the shell font (`Segoe UI Variable Text`, then `Segoe UI`), set through
`WithDefaultFontFamily`, with the bundled Inter as the fallback for machines that have neither. Only
issue keys deviate, and only because they need fixed advance widths.

### Why avatars carry a size class rather than a `Size`

AtomUI's `Avatar` brings Ant Design's fit-to-width behaviour: when the initials are wider than the
avatar less its `Gap` at each end, it scales them down and translates them sideways. The translation
is written for a left-edge transform origin, so under Avalonia it lands the monogram off centre — and
because the threshold is a width comparison, whether an avatar was affected depended on the initials
in it. At this app's sizes the same row could hold `IL` pushed right, `RV` pushed left and `WW`
clipped against the edge, next to a single-letter monogram sitting perfectly centred.

The classes set `Gap` to 0 and pair each diameter with a font of half that diameter. In this font the
widest monogram two capitals can make — `WW` — measures 1.87 times the font size, so half the diameter
keeps even that inside the circle, the scale stays exactly 1, and no transform is applied at all. The
pair has to be set together, which is why it is one class rather than two attributes at each call
site: a new avatar written with a bare `Size="24"` brings the old behaviour back.

## The one theming rule to remember

**A local value beats every style setter.** A value written on the element —
`<Border Background="{atom:SharedTokenResource ColorBgLayout}">` — is a local value, and local values
outrank Style setters, *including* the ones a class turns on. So a column written that way can never
be lit up by adding a class to it, and the highlight silently does nothing.

This is why the resting appearance of the board lane and column, the board card, and the My Issues group is set
in `<UserControl.Styles>` rather than on the elements themselves. Both the default and the drag state
are then Style setters, and the more specific one wins. It is the first thing to check when a class
appears to have no effect.

The same rule cuts the other way and is used deliberately in the issue form: the label chips set their
colours locally on `atom:CheckableTag` precisely so that being checked cannot repaint them in the
primary colour, and each label keeps its own.

### And its corollary: a style beats inheritance

Which is why there is no app-wide `Style Selector="TextBlock"` in `App.axaml`, and why adding one back
would break more than it fixes.

Foreground and FontSize are inherited properties, and the window is an `atom:Window` whose theme sets
both from the tokens — so every `TextBlock` under it already reads `ColorText` at the token's size
without being told. An app-wide setter therefore looks free while changing nothing you can see.

It is not free. AtomUI's own templates rely on that same inheritance: a tooltip paints a dark box, sets
`Foreground` on it, and expects the plain `TextBlock` its `ContentPresenter` builds to pick the colour
up. A Style setter outranks inheritance, so an app-wide one repaints that text in body colour and the
tooltip becomes near-black on near-black. Everything AtomUI colours against its own background is
exposed the same way — menu hover states, notifications, popups — which is to say every surface the app
does not draw itself.

The rule to take from it: style the controls this app puts on screen, by class or by type, and leave
bare `TextBlock` alone. Text that should not be body text says so for itself — `TextBlock.caption`,
`.key`, `.field`.

## The board

The workspace loads the team's workflow states and issues (`?sort=board`), builds one column per
state, and then subscribes to the hub.

### Lanes, and the one that holds two columns

Columns are laid out one per **lane** — one column wide, one column deep — with a single exception:
**Todo sits on top of Backlog in one lane**. What a board is asked to do most often is promote
something out of the backlog, and stacking the two makes that a drag straight up into the column above
rather than a hunt for one somewhere off to the right; the arrangement says where the work is going
before the drag starts.

The two stay two columns while they do it — their own headers, their own counts, their own drop
targets — because they are still two workflow states and a drop has to land in one of them. They split
the lane's height evenly rather than sizing to their contents, so both halves stay on screen and the
drag always has somewhere to go.

The pairing is by `WorkflowStateType` rather than by name (a team may rename its states but cannot
change what they mean), it covers every state of those two types rather than just the two a team starts
with, and the lane takes the leftmost of the positions those states hold — so the rest of the board
keeps the order the team gave it. A team with only one of the two types gets an ordinary
single-column lane, which is exactly what it had before. `BoardViewModel.Lay` is the whole rule, and
`tests/Planner.Client.Checks` pins it.

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
| The lane outlined and the column inside it tinted, both in the primary colour | *Would this column take it?* | `IsDropTarget`, as a class on each — the lane's follows its columns' |
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

## Users and access

Owners and administrators can open **Users & access** from the sidebar, including on an installation
with no teams. Search covers the complete directory, including inactive accounts. Select a user to
edit their display name, time zone, organisation role, active state, and memberships across teams;
archived teams are included and labelled. **New user** also requires an email and a confirmed password
of at least 12 characters. Existing email addresses are read-only.

The team table previews effective access using the API's permission matrix: admins and owners
administer every team, guests are capped at commenting, and a member's Viewer role grants read-only
access. Only owners can assign or revoke ownership. Own accounts and owner accounts cannot be
deactivated through the page. The API remains the authority for every write, including the last-lead
restriction.

**Save user & team access** saves the account followed by changed memberships. These are separate API
operations: if a team change fails, the page reports partial success and keeps the remaining edits for
correction and retry. Password reset is a separate action. Navigation prompts before discarding edits,
and is blocked while an operation is running. F5 reloads the directory. Organisation role and active
state changes become effective at token refresh or sign-in.

## Teams and membership

**Teams** sits beside Users & access in the sidebar, and like it is reachable on an installation with
no teams at all — creating the first one is the point. Who sees the entry is the API's permission
matrix, read off the caller's own profile: an owner or administrator administers every team and is the
only one who can create one, a team lead administers the teams they lead, and a guest administers
nothing whatever their team role says. The list is therefore the teams you may *change*, which is a
different list from the switcher above it — that one is every team you may read.

Archived teams are listed last rather than left out, because restoring one is only possible from a list
that still shows it. Selecting a team opens its settings — name, description, colour, and whether it is
private — with its membership underneath. The key is set once, at creation: it prefixes every issue
identifier the team ever writes, so the server does not let it change and the field is read-only
afterwards. It is upper-cased and checked against the server's own rule (1–8 characters, starting with
a letter) before the request goes out, because a rejected key is otherwise a round trip to learn a
typo.

Creating flows straight into filling in, exactly as the project page does: a successful create turns
the page into that team's page, the creator is already in it as its first lead, and the membership
section below comes to life. Roles are changed in place and saved with the page — each row carries a
tick while it differs from the server, and **Save changes** drives every dirty row as well as the team
itself. Adding someone is its own button and its own request; so is removing them, which asks first and
leaves both the account and everything they wrote in the team untouched. The picker offers active
accounts who are not already members.

Two refusals belong to the server and are shown as it writes them: a duplicate team key, and demoting
or removing a team's only lead. A lead who demotes or removes *themselves* — which the server allows
once another lead exists — loses the team from this page at that moment, because the authority the page
was working with has just been handed back.

Archiving is how a team ends. Deleting one would take its projects, issues and history with it, so the
API does not offer that and neither does this page; an archived team says so, and **Restore** puts it
back. Anything that changes the team list — a create, a rename, a recolour, an archive or a restore —
refreshes the switcher and the sidebar around the page without navigating away from it.

Run the simulated-API regression checks with `dotnet run --project tests/Planner.Client.Checks`. They
cover directory pagination, draft protection, partial-save recovery, membership operations, password
validation, PATCH omission, effective rights, who may administer which teams, key validation and
upper-casing, create-then-fill-in, the last-lead refusal, archive and restore, a lead demoting
themselves, and the board's lane layout. They do not replace an interactive desktop check against a
running API.

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
dialog, creating and editing projects and their milestones on a page, archiving issues, user
administration, team administration and membership, the menu bar and status bar, keyboard activation
and a per-row context menu, live updates, and the full update pipeline. Absent, in rough order of what
a user would miss first:

- **Comments, sub-issues, attachments and the activity feed.** The editor covers an issue's fields;
  everything around the conversation is still API-only (`docs/api.md`).
- Filtering, search and saved views — the API's filter surface is much richer than the UI exposes.
- **A team's board columns and its labels.** The teams page covers a team's settings and who is in it;
  workflow states and labels are administered through the API alone (`docs/api.md`).
- Archiving and restoring projects, which needs somewhere to see archived ones first.
- **Live updates for anything but issues.** The socket carries project, milestone, label and member
  changes too; `RealtimeService` subscribes to `IssueChanged` alone, so a project someone else creates
  appears on the next refresh rather than immediately.
- The document views.
- Offline queueing of writes.
