<#
.SYNOPSIS
    Uploads a built client release to the Planner server and publishes it.

.DESCRIPTION
    Run this straight after build/release.ps1. It stages the files the channel's feed index actually
    references, then runs deploy/publish-release.sh on the server, which validates every package
    against the index and swaps the index in last. Nothing restarts; the API serves these as static
    files, so the next client to ask sees the new version.

    Uploading into a staging directory rather than straight into the live folder is the whole point:
    an index that arrives before its packages tells clients to download something that is not there
    yet. The publish script is uploaded alongside the release, so the server always runs the version
    of it that is committed here.

.PARAMETER Version
    The version just built, e.g. 1.2.0 or 1.2.0-beta.1.

.PARAMETER Channel
    'win' for stable, 'win-beta' for the pilot channel. Must match the -Channel used to build.

.PARAMETER Server
    SSH destination of the VPS, user@host.

.EXAMPLE
    ./build/upload-release.ps1 -Version 1.1.0

.EXAMPLE
    ./build/upload-release.ps1 -Version 1.2.0-beta.1 -Channel win-beta
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('win', 'win-beta')]
    [string]$Channel = 'win',

    [string]$Server = 'root@planner.lyste.net',

    [string]$SiteUrl = 'https://planner.lyste.net',

    [string]$ReleaseDir,

    [string]$RemoteRoot = '/srv/planner',

    # Skip the post-publish check, for a server that is not reachable from this machine over HTTPS.
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $repoRoot 'releases' }

function Write-Step { param([string]$Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Invoke-Ssh {
    param([string]$Command)
    & ssh $Server $Command
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed: $Command" }
}

foreach ($tool in 'ssh', 'scp') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool is not on PATH. Install the Windows OpenSSH client."
    }
}

$index = Join-Path $ReleaseDir "releases.$Channel.json"
$installer = Join-Path $ReleaseDir "Planner-$Channel-Setup.exe"
$portable = Join-Path $ReleaseDir "Planner-$Channel-Portable.zip"
$publisher = Join-Path $repoRoot 'deploy/publish-release.sh'

foreach ($required in $index, $installer, $publisher) {
    if (-not (Test-Path $required)) {
        throw "Not found: $required. Build the release first: ./build/release.ps1 -Version $Version -Channel $Channel"
    }
}

Write-Step "Planner $Version -> $Server (channel '$Channel')"

# The index is the authority on what belongs to this release. Packages it references that are already
# live on the server do not need uploading again; the publish script verifies those in place.
$feed = Get-Content $index -Raw | ConvertFrom-Json
$assets = @()
if ($feed.PSObject.Properties['Assets']) { $assets = @($feed.Assets) }
if (-not $assets) { throw "The feed index lists no packages: $index" }

if (-not ($assets | Where-Object { $_.Version -eq $Version })) {
    throw "The feed index has no $Version entry. Was it built on channel '$Channel'?"
}

$referenced = @($assets | ForEach-Object { $_.FileName })

$packages = $referenced |
    ForEach-Object { Join-Path $ReleaseDir $_ } |
    Where-Object { Test-Path $_ }

$files = @($index, $installer) + $packages
if (Test-Path $portable) { $files += $portable }

$stage = "$RemoteRoot/incoming/$Channel/$Version"

Write-Step "Uploading $($files.Count) files to $stage"
Invoke-Ssh "mkdir -p -- '$stage' '$RemoteRoot/releases'"
& scp -- @files "${Server}:$stage/"
if ($LASTEXITCODE -ne 0) { throw 'Upload failed. Nothing was published; rerun when the transfer succeeds.' }

& scp -- $publisher "${Server}:$RemoteRoot/publish-release.sh"
if ($LASTEXITCODE -ne 0) { throw 'Could not upload the publish script.' }

Write-Step 'Publishing on the server'
Invoke-Ssh "bash '$RemoteRoot/publish-release.sh' --channel '$Channel' '$stage' '$RemoteRoot/releases'"

if (-not $SkipVerify) {
    Write-Step 'Verifying the public feed'
    $feedUrl = "$($SiteUrl.TrimEnd('/'))/updates/releases.$Channel.json"
    $published = Invoke-RestMethod -Uri $feedUrl -Headers @{ 'Cache-Control' = 'no-cache' }

    if (-not ($published.Assets | Where-Object { $_.Version -eq $Version })) {
        throw "The published feed at $feedUrl does not list $Version."
    }

    $setupUrl = "$($SiteUrl.TrimEnd('/'))/updates/Planner-$Channel-Setup.exe"
    $head = Invoke-WebRequest -Uri $setupUrl -Method Head
    # Headers come back as a string on Windows PowerShell and as an array on 7; @() flattens both.
    $size = [long](@($head.Headers['Content-Length'])[0])
    Write-Host "  $feedUrl lists $Version"
    Write-Host "  $setupUrl responds $($head.StatusCode) ($([math]::Round($size / 1MB, 1)) MB)"
}

Write-Step 'Done'
Write-Host "  Installed clients on '$Channel' pick this up on their next check (startup, then every four hours)."
if ($Channel -eq 'win-beta') {
    Write-Host '  Beta testers need "updateChannel": "win-beta" in %AppData%\Planner\settings.json.'
}
