#!/usr/bin/env bash
# Publishes the NativeAOT `shepherd` executable for linux-x64.
#
# Run it on Linux (inside WSL): NativeAOT does not cross-compile between OSes. Requires the
# .NET 10 SDK and clang/zlib (Ubuntu: sudo apt install clang zlib1g-dev).
#
# Usage: scripts/publish.sh [output-dir]      (default: publish/linux-x64)
#
# The repo may live on /mnt/c; building there is slow, so the sources are copied to a temp dir
# first and only the result is copied back.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$here/publish/linux-x64}"
work="$(mktemp -d /tmp/shepherd-publish.XXXXXX)"
trap 'rm -rf "$work"' EXIT

rsync -a --exclude bin --exclude obj --exclude publish --exclude .git --exclude TestResults "$here/" "$work/"
dotnet publish "$work/src/Shepherd.Cli/Shepherd.Cli.csproj" -c Release -r linux-x64 -o "$work/out"

mkdir -p "$out"
cp "$work/out/shepherd" "$work/out/libe_sqlite3.so" "$out/"
chmod +x "$out/shepherd"
ls -la "$out"
