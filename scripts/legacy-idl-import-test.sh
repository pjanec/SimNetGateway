#!/usr/bin/env bash
# Tests of scripts/legacy-idl-import.py on synthetic IDL fixtures (DD-013, T-028). Not part of the .NET suite.
# The pipeline tests need the CycloneDDS.NET tools folder and (offline) a NuGet source with the CycloneDDS.NET package:
#   LEGACY_IDL_TOOLS=<package tools/ folder: idlc + CycloneDDS.IdlImporter.dll>  [LEGACY_IDL_PACKAGE_SOURCE=<folder or URL>]
# Without LEGACY_IDL_TOOLS only the parser tests run. Extra arguments go to unittest.
here="$(cd "$(dirname "$0")/.." && pwd)"
cd "$here" || exit 2
export PATH="$HOME/.dotnet:$PATH"
for py in python3 python; do
  if "$py" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    exec "$py" -m unittest discover -v -s tests/legacy-idl-import -p 'test_*.py' "$@"
  fi
done
echo "no Python 3 interpreter found (tried python3, python)." >&2
exit 2
