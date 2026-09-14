#!/usr/bin/env bash
# Builds, tests and packs the framework into artifacts/packages, then restores the samples against it.
set -euo pipefail
CONFIGURATION="${1:-Release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
dotnet build NetCoreAI.slnx -c "$CONFIGURATION"
if [[ "${SKIP_TESTS:-0}" != "1" ]]; then dotnet test --solution NetCoreAI.slnx --no-build -c "$CONFIGURATION"; fi
dotnet pack NetCoreAI.slnx --no-build -c "$CONFIGURATION" -o artifacts/packages
dotnet restore samples/NetCoreAI.Samples.slnx --force
