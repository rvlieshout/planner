#!/usr/bin/env bash
# Publish a completed Windows Velopack upload, keeping the feed index last.
set -euo pipefail

usage() {
    echo "Usage: bash $0 [--channel NAME] STAGE_DIRECTORY [RELEASE_DIRECTORY]"
    echo "Default channel: win (stable). The beta channel is win-beta."
    echo "Default release directory: /srv/planner/releases"
}
fail() { echo "Error: $*" >&2; exit 1; }

channel=win
while [[ ${1:-} == -* ]]; do
    case "$1" in
        --help|-h)
            usage
            exit 0
            ;;
        --channel)
            [[ -n ${2:-} ]] || fail "--channel needs a value."
            channel=$2
            shift 2
            ;;
        --channel=*)
            channel=${1#--channel=}
            shift
            ;;
        *)
            usage >&2
            exit 2
            ;;
    esac
done

[[ $channel =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || fail "Invalid channel name: $channel"

if (( $# < 1 || $# > 2 )); then
    usage >&2
    exit 2
fi
for tool in python3 flock realpath mktemp; do
    command -v "$tool" >/dev/null || fail "Required command not found: $tool"
done

# Channels share one directory and one public path: Velopack asks for the index of the channel it
# is on, and package filenames carry the version, which build/release.ps1 keeps distinct per channel.
index="releases.$channel.json"
installer="Planner-$channel-Setup.exe"
portable="Planner-$channel-Portable.zip"

stage=$(realpath -e -- "$1")
[[ -d "$stage" ]] || fail "Stage is not a directory: $stage"
live=$(realpath -m -- "${2:-/srv/planner/releases}")
[[ "$stage" != "$live" && "$stage/" != "$live/"* && "$live/" != "$stage/"* ]] \
    || fail "Stage and release directories must be separate and must not contain one another."
mkdir -p -m 0755 -- "$live"

# The lock lives outside the public feed; all publishers must use this script. It is per release
# directory rather than per channel, so two channels are never published into it at once.
exec 9>"${live}.publish.lock"
flock -n 9 || fail "Another publication is already running for $live"

for name in "$index" "$installer"; do
    [[ -f "$stage/$name" && ! -L "$stage/$name" && -s "$stage/$name" ]] \
        || fail "Missing, empty, or symbolic-link file: $stage/$name"
done

# Copy first, so validation covers the exact bytes that will be published. Creating
# the temporary directory inside live guarantees same-filesystem atomic renames.
pending=$(mktemp -d -- "$live/.publish.XXXXXXXX")
trap 'rm -rf -- "$pending"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
cp -- "$stage/$index" "$stage/$installer" "$pending/"
shopt -s nullglob
for file in "$stage"/*.nupkg "$stage/$portable"; do
    [[ -e "$file" ]] || continue
    [[ -f "$file" && ! -L "$file" && -s "$file" ]] || fail "Invalid release file: $file"
    cp -- "$file" "$pending/"
done

# An incomplete upload must never replace the public index. A referenced package
# may already be live; verify both its size and SHA-256 against the incoming feed.
python3 - "$pending" "$live" "$index" <<'PY'
import hashlib
import json
from pathlib import Path
import re
import sys

pending, live = map(Path, sys.argv[1:3])
index = sys.argv[3]
try:
    assets = json.loads((pending / index).read_text(encoding="utf-8-sig"))["Assets"]
    if not isinstance(assets, list) or not assets:
        raise ValueError("The feed must contain a nonempty Assets array.")
    names = set()
    full = False
    for asset in assets:
        name = asset["FileName"]
        if not isinstance(name, str) or not re.fullmatch(r"Planner-[A-Za-z0-9.+_-]+\.nupkg", name):
            raise ValueError(f"Invalid package filename: {name!r}")
        if name in names:
            raise ValueError(f"Duplicate package: {name}")
        names.add(name)
        if asset.get("PackageId") != "Planner" or asset.get("Type") not in ("Full", "Delta"):
            raise ValueError(f"Unexpected package identity or type: {name}")
        full |= asset["Type"] == "Full"
        size, digest = asset["Size"], asset["SHA256"]
        if type(size) is not int or size <= 0 or not isinstance(digest, str) or not re.fullmatch(r"[a-fA-F0-9]{64}", digest):
            raise ValueError(f"Invalid size or SHA256: {name}")
        file = pending / name if (pending / name).exists() else live / name
        if file.is_symlink() or not file.is_file():
            raise ValueError(f"Missing package: {name}")
        checksum = hashlib.sha256()
        with file.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                checksum.update(chunk)
        actual = checksum.hexdigest()
        if file.stat().st_size != size or actual.lower() != digest.lower():
            raise ValueError(f"Package size or SHA256 mismatch: {name}")
    if not full:
        raise ValueError("The feed must contain a full package.")
except (KeyError, TypeError, ValueError, OSError) as error:
    sys.exit(f"Error: feed validation failed: {error}")
PY

# Preflight every immutable package before changing any public file.
for file in "$pending"/*.nupkg; do
    target="$live/$(basename -- "$file")"
    if [[ -e "$target" || -L "$target" ]]; then
        [[ -f "$target" && ! -L "$target" ]] || fail "Invalid existing package: $target"
        cmp -s -- "$file" "$target" || fail "Existing package differs: $target. Use a new version."
    fi
done
chmod 644 -- "$pending"/*
for file in "$pending"/*.nupkg; do
    target="$live/$(basename -- "$file")"
    [[ -e "$target" ]] || mv -- "$file" "$target"
done
mv -fT -- "$pending/$installer" "$live/$installer"
if [[ -f "$pending/$portable" ]]; then
    mv -fT -- "$pending/$portable" "$live/$portable"
fi
mv -fT -- "$pending/$index" "$live/$index"
echo "Published $stage -> $live (channel $channel)"
echo "Incoming files and old live packages were retained. No service restart is needed."
