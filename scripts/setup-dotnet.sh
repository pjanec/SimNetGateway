#!/usr/bin/env bash
# Ensures a .NET 8 SDK is available (Linux cloud VMs often have none).
# Installs it per-user into ~/.dotnet with Microsoft's dotnet-install.sh when missing.
# Usage: bash scripts/setup-dotnet.sh   then   export PATH="$HOME/.dotnet:$PATH"
set -euo pipefail
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^8\.0\.'; then
  echo ".NET 8 SDK already available: $(dotnet --list-sdks | grep '^8\.0\.' | tail -1)"
  exit 0
fi
if [ -x "$HOME/.dotnet/dotnet" ] && "$HOME/.dotnet/dotnet" --list-sdks | grep -q '^8\.0\.'; then
  echo ".NET 8 SDK found in ~/.dotnet - add it to PATH: export PATH=\"\$HOME/.dotnet:\$PATH\""
  exit 0
fi
tmp="$(mktemp -d)"
curl -sSL https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh"
bash "$tmp/dotnet-install.sh" --channel 8.0 --install-dir "$HOME/.dotnet"
rm -rf "$tmp"
echo "Installed. Run: export PATH=\"\$HOME/.dotnet:\$PATH\""
