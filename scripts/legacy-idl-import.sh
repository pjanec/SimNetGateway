#!/usr/bin/env bash
# Portable entry point for scripts/legacy-idl-import.py (DD-013): picks a working Python 3 on Linux and Windows (Git Bash).
# Usage: bash scripts/legacy-idl-import.sh --input <dir|files> --include-dir <dir>... --work <dir> --output <dir> --tools <dir> [--verify] ...
here="$(cd "$(dirname "$0")" && pwd)"
for py in python3 python; do
  if "$py" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    exec "$py" "$here/legacy-idl-import.py" "$@"
  fi
done
echo "legacy-idl-import: no Python 3 interpreter found (tried python3, python)." >&2
exit 2
