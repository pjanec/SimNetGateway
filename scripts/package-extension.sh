#!/usr/bin/env bash
# Publishes the gateway as a HROT node extension into out/extensions/simnetgateway/ (or the folder given as $1).
#
# Layout (DD-016):
#   <target>/SimNetGateway.HrotHost.dll + the shared assemblies (engine, gateway, protocols)   the host
#   <target>/plugins/<name>/plugin.json + the plugin's own assemblies                           one folder per side
#   <target>/plugins/hla/rti/<name>/                                                            one folder per RTI binding of the HLA plugin
# A plugin folder holds only what the host (and, for an add-on, its plugin) does not already hold: the host's copy is the one type identity.
# The folder holds NO Hrot.*, Fdp.* or CycloneDDS* file: the host process provides them; this script fails when one is in the result, because HROT would
# refuse the folder at start.
#
# More plugins (a product that builds on this repository) come in through two environment variables:
#   SIMNETGATEWAY_EXTRA_PLUGINS  lines "<csproj>|<folder under the target>|<assembly name>[|<parent folder>]"
#   SIMNETGATEWAY_PACKAGE_HOOK   a script called with the target folder after the plugins are in, before the result is checked
# HrotRoot: the HROT checkout to build against (environment variable HROT_ROOT or HrotRoot).
# Usage: bash scripts/package-extension.sh [target-folder]   (extra dotnet arguments after "--")
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$PATH"
target="${1:-$here/out/extensions/simnetgateway}"
shift || true
[ "${1:-}" = "--" ] && shift

rm -rf "$target"
mkdir -p "$target"
dotnet publish "$here/src/Hosts/SimNetGateway.HrotHost/SimNetGateway.HrotHost.csproj" -c Release -o "$target" --nologo -v q "$@"

# add_plugin <csproj> <folder under target> <assembly> [parent folder under target]
add_plugin() {
  local csproj="$1" folder="$2" assembly="$3" parent="${4:-}"
  local tmp dest
  tmp="$(mktemp -d)"
  dest="$target/$folder"
  dotnet publish "$csproj" -c Release -o "$tmp" --nologo -v q "${@:5}" >/dev/null
  mkdir -p "$dest"
  for f in "$tmp"/*; do
    local name
    name="$(basename "$f")"
    case "$name" in *.deps.json|*.runtimeconfig.json) continue ;; esac
    [ -e "$target/$name" ] && continue
    [ -n "$parent" ] && [ -e "$target/$parent/$name" ] && continue
    cp "$f" "$dest/$name"
  done
  rm -rf "$tmp"
  [ -f "$dest/$assembly" ] || { echo "package-extension: $assembly is missing from $dest" >&2; exit 1; }
  printf '{ "assembly": "%s" }\n' "$assembly" > "$dest/plugin.json"
}

add_plugin "$here/src/Hosts/SimNetGateway.Plugin.Dis/SimNetGateway.Plugin.Dis.csproj" plugins/dis SimNetGateway.Plugin.Dis.dll "" "$@"
add_plugin "$here/src/Hosts/SimNetGateway.Plugin.Hla/SimNetGateway.Plugin.Hla.csproj" plugins/hla SimNetGateway.Plugin.Hla.dll "" "$@"
add_plugin "$here/src/Hosts/SimNetGateway.Plugin.Hla.Rti.Fake/SimNetGateway.Plugin.Hla.Rti.Fake.csproj" plugins/hla/rti/fake SimNetGateway.Plugin.Hla.Rti.Fake.dll plugins/hla "$@"

if [ -n "${SIMNETGATEWAY_EXTRA_PLUGINS:-}" ]; then
  while IFS='|' read -r csproj folder assembly parent; do
    [ -z "$csproj" ] && continue
    add_plugin "$csproj" "$folder" "$assembly" "${parent:-}" "$@"
  done <<< "$SIMNETGATEWAY_EXTRA_PLUGINS"
fi

if [ -n "${SIMNETGATEWAY_PACKAGE_HOOK:-}" ]; then
  bash "$SIMNETGATEWAY_PACKAGE_HOOK" "$target"
fi

bad="$(find "$target" \( -iname 'Hrot.*' -o -iname 'Fdp.*' -o -iname 'CycloneDDS*' -o -iname 'libddsc*' -o -iname 'ddsc.*' -o -type d -name runtimes \) | sort)"
if [ -n "$bad" ]; then
  echo "package-extension: the folder holds files that belong to the host:" >&2
  echo "$bad" >&2
  exit 1
fi
if [ ! -f "$target/SimNetGateway.HrotHost.dll" ]; then
  echo "package-extension: SimNetGateway.HrotHost.dll is missing from $target" >&2
  exit 1
fi
echo "package-extension: $target"
(cd "$target" && find . -type f ! -name '*.pdb' | sort | sed 's|^\./|  |')
