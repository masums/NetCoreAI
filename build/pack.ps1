#!/usr/bin/env pwsh
# Builds, tests and packs the framework into artifacts/packages, then restores the samples against it.
param([string]$Configuration = "Release", [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $root
try {
  # ErrorActionPreference does not apply to a native exit code, so every step is checked by hand.
  # Without this a failed pack printed its error, the script carried on, and the samples restored
  # against a feed quietly missing a package the meta-package depends on.
  dotnet build "NetCoreAI.slnx" -c $Configuration
  if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

  if (-not $SkipTests) {
    dotnet test --solution "NetCoreAI.slnx" --no-build -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
  }

  dotnet pack "NetCoreAI.slnx" --no-build -c $Configuration -o "artifacts/packages"
  if ($LASTEXITCODE -ne 0) { throw "pack failed ($LASTEXITCODE)" }

  dotnet restore "samples/NetCoreAI.Samples.slnx" --force
  if ($LASTEXITCODE -ne 0) { throw "the samples could not restore against the packed feed ($LASTEXITCODE)" }
} finally { Pop-Location }
