<#
.SYNOPSIS
    Builds a Planner desktop client release and adds it to the Velopack update feed.

.DESCRIPTION
    One command produces everything a release needs:

      Planner-{version}-full.nupkg   the update package existing installs download
      Planner-{version}-delta.nupkg  the difference from the previous release, when there is one
      Planner-win-Setup.exe          the installer for a machine that has nothing yet
      Planner-win-Portable.zip       a no-install copy, for locked-down machines
      releases.win.json              the feed index the client reads

    The release folder accumulates. Keep it — vpk needs the previous packages present to build the
    delta, and a client on an old version needs the packages between here and there.

.PARAMETER Version
    Semantic version for this release, e.g. 1.2.0. Must be higher than the last one: Velopack will not
    offer a downgrade, so a mistyped lower version simply never reaches anyone.

.PARAMETER PublishTo
    Optional destination to copy the finished feed to: a UNC share, a mounted path, or the server's
    ./releases folder. Omit to build locally and copy it yourself.

.EXAMPLE
    ./build/release.ps1 -Version 1.1.0

.EXAMPLE
    ./build/release.ps1 -Version 1.1.0 -ReleaseNotes ./CHANGELOG-1.1.0.md -PublishTo \\fileserver\planner\releases
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$Channel = 'win',

    [string]$Runtime = 'win-x64',

    [string]$Configuration = 'Release',

    [string]$ReleaseDir,

    [string]$ReleaseNotes,

    [string]$PublishTo,

    # Ship without needing .NET installed on the target machine. Turn this off only if you also add
    # --framework net10.0-x64-desktop to the vpk call, so Velopack installs the runtime instead.
    [bool]$SelfContained = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Planner.Client/Planner.Client.csproj'
$icon = Join-Path $repoRoot 'src/Planner.Client/Assets/planner.ico'

if (-not $ReleaseDir) { $ReleaseDir = Join-Path $repoRoot 'releases' }
$publishDir = Join-Path $repoRoot "artifacts/publish/$Version-$Runtime"

function Write-Step { param([string]$Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }

Write-Step "Planner client $Version ($Runtime, channel '$Channel')"

# vpk is pinned in .config/dotnet-tools.json, so every machine builds with the same packer.
Write-Step 'Restoring build tools'
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

Write-Step 'Publishing the client'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
    '-o', $publishDir,
    "-p:Version=$Version",
    "-p:AssemblyVersion=$($Version -replace '-.*$', '').0",
    "-p:FileVersion=$($Version -replace '-.*$', '').0"
)

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$mainExe = if ($Runtime -like 'win*') { 'Planner.Client.exe' } else { 'Planner.Client' }
if (-not (Test-Path (Join-Path $publishDir $mainExe))) {
    throw "Published output has no $mainExe. Check the runtime identifier."
}

Write-Step 'Packing the release'
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

$packArgs = @(
    'vpk', 'pack',
    '--packId', 'Planner',
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--packTitle', 'Planner',
    '--packAuthors', 'Planner',
    '--mainExe', $mainExe,
    '--outputDir', $ReleaseDir,
    '--channel', $Channel
)

if (Test-Path $icon) { $packArgs += @('--icon', $icon) }

if ($ReleaseNotes) {
    if (-not (Test-Path $ReleaseNotes)) { throw "Release notes file not found: $ReleaseNotes" }
    $packArgs += @('--releaseNotes', (Resolve-Path $ReleaseNotes).Path)
}

# Sign here in a real deployment. Windows SmartScreen treats unsigned installers harshly, and an
# update the user is scared to accept is an update that does not get installed:
#   $packArgs += @('--signTemplate', 'signtool sign /fd sha256 /tr http://timestamp.digicert.com {{file}}')

dotnet @packArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

if ($PublishTo) {
    Write-Step "Publishing the feed to $PublishTo"
    New-Item -ItemType Directory -Force -Path $PublishTo | Out-Null

    # The whole folder, not just the new files: clients on older versions still need the packages in
    # between, and the feed index has to arrive alongside them.
    Copy-Item -Path (Join-Path $ReleaseDir '*') -Destination $PublishTo -Recurse -Force
}

Write-Step 'Done'

$feed = Join-Path $ReleaseDir "releases.$Channel.json"
Write-Host "  Feed index : $feed"
Write-Host "  Packages   : $ReleaseDir"

Get-ChildItem $ReleaseDir -Filter "*$Version*" | ForEach-Object {
    Write-Host ('  {0,-46} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB))
}

if (-not $PublishTo) {
    Write-Host "`n  Next: copy the contents of $ReleaseDir to the server's update directory" -ForegroundColor Yellow
    Write-Host "        (the folder bound to /var/lib/planner/updates; PLANNER_UPDATE_DIR in .env)."
}
