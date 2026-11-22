#!/usr/bin/env bash
# Tests the whole solution (Linux, macOS, Windows Git Bash). Extra arguments go to 'dotnet test'.
# Uses the per-user SDK of scripts/setup-dotnet.sh when no dotnet is on PATH.
set -euo pipefail
cd "$(dirname "$0")/.."
# No MSBuild node reuse: nested dotnet publish (package-extension.sh, run by the HrotHost tests) leaves reused nodes holding the
# caller's output pipe and the run hangs (found 2026-11-22, T-062).
export MSBUILDDISABLENODEREUSE=1
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ] || [ -x "$HOME/.dotnet/dotnet.exe" ]; then
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "dotnet not found - run: bash scripts/setup-dotnet.sh" >&2
    exit 1
  fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet test SimNetGateway.sln "$@"
