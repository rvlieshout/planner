#!/usr/bin/env bash
# Publish a completed Windows Velopack upload, keeping the feed index last.
set -euo pipefail

usage() {
    echo "Usage: bash $0 STAGE_DIRECTORY [RELEASE_DIRECTORY]"
    echo "Default release directory: /srv/planner/releases"
}
fail() { echo "Error: $*" >&2; exit 1; }

if [[ ${1:-} == --help || ${1:-} == -h ]]; then
    usage
    exit 0
fi
if (( $# < 1 || $# > 2 )); then
    usage >&2
    exit 2
fi
for tool in python3 flock realpath mktemp; do
    command -v "$tool" >/dev/null || fail "Required command not found: $tool"
done

stage=$(realpath -e -- "$1")
[[ -d "$stage" ]] || fail "Stage is not a directory: $stage"
live=$(realpath -m -- "${2:-/srv/planner/releases}")
[[ "$stage" != "$live" && "$stage/" != "$live/"* && "$live/" != "$stage/"* ]] \
    || fail "Stage and release directories must be separate and must not contain one another."
mkdir -p -m 0755 -- "$live"

# The lock lives outside the public feed; all publishers must use this script.
exec 9>"${live}.publish.lock"
flock -n 9 || fail "Another publication is already running for $live"

for name in releases.win.json Planner-win-Setup.exe; do
    [[ -f "$stage/$name" && ! -L "$stage/$name" && -s "$stage/$name" ]] \
        || fail "Missing, empty, or symbolic-link file: $stage/$name"
done

# Copy first, so validation covers the exact bytes that will be published. Creating
# the temporary directory inside live guarantees same-filesystem atomic renames.
pending=$(mktemp -d -- "$live/.publish.XXXXXXXX")
trap 'rm -rf -- "$pending"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
cp -- "$stage/releases.win.json" "$stage/Planner-win-Setup.exe" "$pending/"
shopt -s nullglob
for file in "$stage"/*.nupkg "$stage/Planner-win-Portable.zip"; do
    [[ -e "$file" ]] || continue
    [[ -f "$file" && ! -L "$file" && -s "$file" ]] || fail "Invalid release file: $file"
    cp -- "$file" "$pending/"
done

# An incomplete upload must never replace the public index. A referenced package
# may already be live; verify both its size and SHA-256 against the incoming feed.
python3 - "$pending" "$live" <<'PY'
import hashlib
import json
from pathlib import Path
import re
import sys

pending, live = map(Path, sys.argv[1:])
try:
    assets = json.loads((pending / "releases.win.json").read_text(encoding="utf-8-sig"))["Assets"]
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
mv -fT -- "$pending/Planner-win-Setup.exe" "$live/Planner-win-Setup.exe"
if [[ -f "$pending/Planner-win-Portable.zip" ]]; then
    mv -fT -- "$pending/Planner-win-Portable.zip" "$live/Planner-win-Portable.zip"
fi
mv -fT -- "$pending/releases.win.json" "$live/releases.win.json"
echo "Published $stage -> $live"
echo "Incoming files and old live packages were retained. No service restart is needed."
