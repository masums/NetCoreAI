#!/usr/bin/env bash
# Builds, tests and packs the framework into artifacts/packages, then restores the samples against it.
set -euo pipefail
CONFIGURATION="${1:-Release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
dotnet build NetCoreAI.slnx -c "$CONFIGURATION"
if [[ "${SKIP_TESTS:-0}" != "1" ]]; then dotnet test --solution NetCoreAI.slnx --no-build -c "$CONFIGURATION"; fi
dotnet pack NetCoreAI.slnx --no-build -c "$CONFIGURATION" -o artifacts/packages
# Drop the cached extraction of every NetCoreAI package before restoring the samples. Versions come from
# commit height, so two packs at the same commit produce the same version number with different content —
# and NuGet, which caches by version id and never re-extracts, then hands the samples the older build.
# That is silent: everything restores, builds and runs, against code from a previous pack.
rm -rf "${NUGET_PACKAGES:-$HOME/.nuget/packages}"/netcoreai*

dotnet restore samples/NetCoreAI.Samples.slnx --force
