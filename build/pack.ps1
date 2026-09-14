#!/usr/bin/env pwsh
# Builds, tests and packs the framework into artifacts/packages, then restores the samples against it.
param([string]$Configuration = "Release", [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $root
try {
  dotnet build "NetCoreAI.slnx" -c $Configuration
  if (-not $SkipTests) { dotnet test --solution "NetCoreAI.slnx" --no-build -c $Configuration }
  dotnet pack "NetCoreAI.slnx" --no-build -c $Configuration -o "artifacts/packages"
  dotnet restore "samples/NetCoreAI.Samples.slnx" --force
} finally { Pop-Location }
