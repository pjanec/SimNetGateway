#!/usr/bin/env bash
# Vendorability dry run (R-092, DD-007 section 2, DD-006 section 14 / H3).
#
# HROT vendors six projects of this repository (Engine, Common, Protocol.Dis7, Protocol.Hrot, Gateway.HrotDis, Transport.DisUdp) with our
# root Directory.Build.props into FDP/ExtDeps/SimNetGateway, below HROT's own props / targets / central package management / editorconfig.
# This script copies exactly that set into a scratch folder nested under a FOREIGN parent that contains hostile build files, builds every
# one of the six projects with 'dotnet build' and fails unless each build succeeds with 0 warnings and 0 errors.
#
# Usage: bash scripts/vendor-dry-run.sh [--keep] [scratch-parent-dir]
#   --keep   do not delete the scratch folder afterwards (it is printed)
# Works on Linux, macOS and Windows Git Bash; nothing outside the scratch folder is written.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ] || [ -x "$HOME/.dotnet/dotnet.exe" ]; then
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "dotnet not found - run: bash scripts/setup-dotnet.sh" >&2
    exit 1
  fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

KEEP=0
if [ "${1:-}" = "--keep" ]; then KEEP=1; shift; fi
PARENT="${1:-$(mktemp -d "${TMPDIR:-/tmp}/simnetgateway-vendor.XXXXXX")}"
mkdir -p "$PARENT"

PROJECTS=(
  "src/Engine/SimNetGateway.Engine"
  "src/Common/SimNetGateway.Common"
  "src/Protocols/SimNetGateway.Protocol.Dis7"
  "src/Protocols/SimNetGateway.Protocol.Hrot"
  "src/Gateways/SimNetGateway.Gateway.HrotDis"
  "src/Transports/SimNetGateway.Transport.DisUdp"
)

FOREIGN="$PARENT/foreign"
VENDOR="$FOREIGN/FDP/ExtDeps/SimNetGateway"
mkdir -p "$VENDOR"

# ---- the hostile parent: everything a host repository may have above the vendored folder ----------------------------------------------
cat > "$FOREIGN/Directory.Build.props" <<'EOF'
<Project>
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsAsErrors>nullable</WarningsAsErrors>
  </PropertyGroup>
</Project>
EOF
cat > "$FOREIGN/Directory.Build.targets" <<'EOF'
<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningLevel>9999</WarningLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
EOF
cat > "$FOREIGN/Directory.Packages.props" <<'EOF'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
EOF
cat > "$FOREIGN/.editorconfig" <<'EOF'
root = true

[*.cs]
dotnet_analyzer_diagnostic.severity = error
dotnet_style_qualification_for_field = true:error
csharp_style_namespace_declarations = block_scoped:error
EOF

# ---- copy exactly the vendored set (sources, project files, LICENSE / NOTICE.md; no bin / obj) -------------------------------------------
cp "$ROOT/Directory.Build.props" "$VENDOR/Directory.Build.props"
cp "$ROOT/.editorconfig" "$VENDOR/.editorconfig"
for p in "${PROJECTS[@]}"; do
  mkdir -p "$VENDOR/$p"
  tar -C "$ROOT/$p" --exclude=bin --exclude=obj -cf - . | tar -C "$VENDOR/$p" -xf -
done

# ---- build the six -------------------------------------------------------------------------------------------------------------------
FAILED=0
for p in "${PROJECTS[@]}"; do
  name="$(basename "$p")"
  log="$PARENT/$name.log"
  if dotnet build "$VENDOR/$p/$name.csproj" --no-incremental -nologo >"$log" 2>&1 \
     && grep -q "0 Warning(s)" "$log" && grep -q "0 Error(s)" "$log"; then
    echo "ok      $name (0 warnings, 0 errors)"
  else
    echo "FAILED  $name - see $log"
    grep -E "warning|error" "$log" | sort -u | head -20 || true
    FAILED=1
  fi
done

if [ "$FAILED" -eq 0 ] && [ "$KEEP" -eq 0 ]; then
  rm -rf "$PARENT"
else
  echo "scratch folder kept: $PARENT"
fi
if [ "$FAILED" -ne 0 ]; then
  echo "vendor dry run FAILED" >&2
  exit 1
fi
echo "vendor dry run OK: all ${#PROJECTS[@]} projects build under a hostile parent"
