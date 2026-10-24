#!/usr/bin/env bash
# Portable entry point for the SessionStart hook and /relearn: picks a working Python 3
# interpreter on Linux and Windows (Git Bash), where the names differ ("python3" may be
# missing, or on Windows be a Store stub that does nothing).
cd "$(dirname "$0")/.." || exit 0
for py in python3 python; do
  if "$py" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    exec "$py" scripts/session_brief.py
  fi
done
echo "!! session brief unavailable: no Python 3 interpreter found (tried python3, python)."
echo "!! Read docs/RULINGS.md, docs/TASKS.md and the latest docs/design/DD-*.md by hand before working."
