#!/usr/bin/env bash
# Rebuilds the DPS contract and copies the IDL it generates to docs/clients/idl/ (DD-019 3.6), the files a client without a .NET build uses.
# Each copy gets the first-line header with the protocol version, loses the generator's timestamp line (it changes at every build) and uses LF line endings.
# Run it after every change to src/Protocols/SimNetGateway.Protocol.DdsPlayer; the test IdlSnapshotTests fails until the committed files match.
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ] || [ -x "$HOME/.dotnet/dotnet.exe" ]; then
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "dotnet not found - run: bash scripts/setup-dotnet.sh" >&2
    exit 1
  fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
project=src/Protocols/SimNetGateway.Protocol.DdsPlayer
dotnet build "$project" -c Debug --nologo -v q
major=$(sed -n 's/.*Major = \([0-9]*\), Minor = \([0-9]*\);.*/\1/p' "$project/DpsProtocol.cs")
minor=$(sed -n 's/.*Major = \([0-9]*\), Minor = \([0-9]*\);.*/\2/p' "$project/DpsProtocol.cs")
out=docs/clients/idl
mkdir -p "$out"
for name in DpsTopics DpsTypes; do
  src="$project/bin/Debug/net8.0/$name.idl"
  [ -f "$src" ] || { echo "missing $src" >&2; exit 1; }
  {
    echo "// DPS protocol $major.$minor, generated from SimNetGateway.Protocol.DdsPlayer - do not edit"
    tr -d '\r' < "$src" | grep -v '^// Generated on:'
  } > "$out/$name.idl"
  echo "wrote $out/$name.idl"
done
