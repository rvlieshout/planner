# Desktop client

An Avalonia 12 client on .NET 10, in `src/Planner.Client`. It signs in against the on-prem API, shows
a team's board, keeps it live over SignalR, and keeps itself up to date through Velopack.

## Running it in development

```bash
docker compose up -d           # the API and database
dotnet run --project src/Planner.Client
```

Sign in with the bootstrap owner from your `.env` (`PLANNER_OWNER_EMAIL` /
`PLANNER_OWNER_PASSWORD`). The server field defaults to `http://localhost:8080` and is remembered.

Development builds are not Velopack installs, so the update service reports that updates are managed
by your IDE and does not run its loop. Everything else behaves identically. To exercise updates for
real, see [releasing.md](releasing.md).

## Shape

```
Program.cs            VelopackApp.Run() first, then Avalonia
App.axaml.cs          DI container, theme resources, window
Services/             the parts with no UI: settings, session, API, realtime, updates
ViewModels/           shell, login, workspace, board, update banner
Views/                one .axaml per view model, matched by ViewLocator
Infrastructure/       app paths, file logging, the Velopack logging bridge
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

## The board

The workspace loads the team's workflow states and issues (`?sort=board`), builds one column per
state, and then subscribes to the hub.

Live changes are applied in place rather than by refetching. `Created` and `Updated` are handled as
the same upsert, which makes the client idempotent: a duplicate delivery or a replay after
reconnecting cannot corrupt the view. `Archived` and `Deleted` remove the card. A change for a team
other than the one on screen is ignored.

The dot next to the team picker is green while the socket is up and grey when it is not. The board
still works when it is grey — it is simply as fresh as the last fetch.

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
switching, the board, live updates, and the full update pipeline. Absent, in rough order of what a
user would miss first:

- Creating and editing issues from the client — the API supports all of it (`docs/api.md`), the UI does not.
- Drag-and-drop between columns. `PlannerApiClient.MoveIssueAsync` is already there and unused.
- The issue detail pane: description, comments, sub-issues, attachments, activity.
- Project, milestone and document views.
- Filtering and saved views.
- Offline queueing of writes.
