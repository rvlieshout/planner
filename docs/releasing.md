# Releasing and updating the desktop client

Distribution is [Velopack](https://velopack.io): the client packs into a small installer, publishes
delta packages, and updates itself from a feed the Planner API already serves. Nothing leaves your
network, and there is no external service to sign up for.

## The short version

```powershell
./build/release.ps1 -Version 1.1.0
```

Then copy `./releases` to the server's update directory. Every running client picks it up within four
hours; anyone restarting picks it up immediately.

## What a release consists of

`build/release.ps1` publishes the client and hands it to `vpk`, producing:

| File | Purpose |
| --- | --- |
| `Planner-{version}-full.nupkg` | The complete package. What a client downloads when no delta applies. |
| `Planner-{version}-delta.nupkg` | Only what changed since the previous release. Typically a few hundred KB against ~50 MB. |
| `Planner-win-Setup.exe` | Installer for a machine with nothing on it yet. |
| `Planner-win-Portable.zip` | Unpacked copy for machines where installers are not allowed. |
| `releases.win.json` | The feed index. The client reads this first. |

**Keep the whole folder.** `vpk` needs the previous packages present to build a delta against them,
and a client that has been offline for three releases needs the packages in between. Releasing into an
empty folder still works — it just means every client downloads a full package.

`vpk` is pinned in `.config/dotnet-tools.json`, so every machine that builds a release uses the same
packer. `dotnet tool restore` runs automatically as part of the script.

## Distributing

The API serves the feed as static files at `/updates`, from the directory bound in `docker-compose.yml`:

```yaml
volumes:
  - ${PLANNER_UPDATE_DIR:-./releases}:/var/lib/planner/updates:ro
```

Releasing is therefore "get the folder onto the server":

```powershell
# straight to a share the server mounts
./build/release.ps1 -Version 1.1.0 -PublishTo \\fileserver\planner\releases

# or copy it yourself
rsync -av releases/ planner-server:/srv/planner/releases/
```

Nothing needs restarting. Static files are read per request, so the next client to ask sees the new
version.

For the Ubuntu demo VPS, upload to an incoming directory first, then use the publication script
to validate package sizes/checksums and publish the index last:

```bash
bash /opt/planner/deploy/publish-release.sh /srv/planner/incoming/1.1.0
# Optional second argument: a different release directory.
```

The incoming files and existing release packages are retained. See
[the VPS protocol](deploy-vps-demo.md#5-build-and-publish-the-windows-client) for prerequisites
and the complete upload procedure.

The feed is **anonymous, by design**. The client checks for updates before anyone signs in — that is
the whole point of the feature, since a release that broke sign-in has to be replaceable — so a token
requirement here would defeat it. What is exposed is the same set of installers you would put on a
share: no customer data.

Other ways to host it, all supported without code changes:

- **A file share.** Put `\\fileserver\planner\releases` in the client's `updateFeedUrl`. Velopack reads
  a path as happily as a URL.
- **Any static web server.** nginx, IIS, an S3 bucket — it is a folder of files.
- **GitHub / Gitea / GitLab releases.** Swap `SimpleWebSource` for `GithubSource` in
  `UpdateService.TryGetManager`; Velopack ships the sources.

## How a client updates

1. On startup, and every four hours thereafter, `UpdateService` fetches `releases.{channel}.json`.
2. A higher version than the running one means: download it now, in the background, resuming from a
   delta when one applies.
3. Once staged, the banner appears — on whatever screen is showing, signed in or not.
4. **The user chooses when.** "Restart and install" applies it and relaunches. Nothing restarts by
   itself; a desktop app that closes underneath someone is a bug, not a feature.
5. If the app is closed before the user says yes, the staged update is still there next launch.

Failures are quiet: an unreachable feed is logged and retried on the next tick. A laptop that spends a
week off the network is not nagged.

## Versioning

Semantic, and it must go up. Velopack does not offer a downgrade, so a version lower than what a client
already runs simply never reaches it.

`-Version` flows into `dotnet publish -p:Version=…` and into `vpk --packVersion`, so the assembly
version, the package and the feed can never disagree about what a build is.

Pre-release suffixes work: `1.2.0-rc.1`.

## Channels

The default channel is `win`. A second channel is a parallel feed in the same folder:

```powershell
./build/release.ps1 -Version 1.2.0-rc.1 -Channel beta
```

Point a pilot group at it by setting `"updateChannel": "beta"` in their `settings.json`. They get
`releases.beta.json`; everyone else keeps seeing `releases.win.json` and never sees the release
candidate.

## Rolling back

There is no "undo release". Because clients refuse downgrades, a bad build is corrected by shipping a
**higher** version containing the previous code:

```powershell
git revert <bad commit>
./build/release.ps1 -Version 1.1.2      # 1.1.1 was the bad one
```

Delete the bad packages from the feed as well, so a machine that has not checked in yet never sees
them. Anyone who already installed it gets 1.1.2 on their next check — which is exactly why the update
banner is not gated on being signed in.

## Code signing

The script ships unsigned and says so in its output. On Windows, SmartScreen treats an unsigned
installer harshly, and an update people are afraid to accept is an update that does not get installed.
Uncomment the signing line in `build/release.ps1`:

```powershell
$packArgs += @('--signTemplate', 'signtool sign /fd sha256 /tr http://timestamp.digicert.com {{file}}')
```

`vpk` substitutes `{{file}}` per file, so any signing tool that takes a path works, including one that
talks to an HSM.

## Self-contained or not

The script publishes self-contained by default: ~50 MB per release, and the target machine needs no
.NET installed. That is usually the right trade for on-prem, where you may not control what is on a
workstation.

To ship framework-dependent builds instead, pass `-SelfContained $false` **and** add
`--framework net10.0-x64-desktop` to the `vpk pack` arguments, so Velopack installs the runtime as part
of setup. Packages drop to a few MB; setup gains a prerequisite step.

## Verifying a release

```bash
curl -s http://localhost:8080/updates/releases.win.json | jq '.Assets[] | {Version, FileName, Size}'
```

The client logs every decision it makes to `%AppData%\Planner\logs\planner-{date}.log`:

```
[inf] UpdateService: Update service starting. Version 1.0.0, feed http://localhost:8080/updates, interval 04:00:00
[inf] UpdateService: Update 1.1.0 available; downloading
[inf] UpdateService: Update 1.1.0 downloaded and staged
```

## Local end-to-end test

Without installing anything on your machine, using Velopack's own test locator:

```csharp
var locator = new TestVelopackLocator("Planner", "1.0.0", stagingDir, logger);
using var updates = new UpdateService(settings, logger, locator);
await updates.CheckAsync();
// updates.Status.State == UpdateState.ReadyToRestart, AvailableVersion == "1.1.0"
```

`UpdateService` takes an optional `IVelopackLocator` for exactly this. For a full test, run
`Planner-win-Setup.exe`, release a higher version, and watch the installed client pick it up.
